#include "PwaKernelShield.h"
#include <ntstrsafe.h>
#include <wdmsec.h>

static UNICODE_STRING g_DeviceName = RTL_CONSTANT_STRING(L"\\Device\\PwaKernelShield");
static UNICODE_STRING g_DosName = RTL_CONSTANT_STRING(L"\\DosDevices\\PwaKernelShield");
static FAST_MUTEX g_TargetLock;
static PWA_TARGET_INFO g_Target;
static BOOLEAN g_TargetValid;
static PEPROCESS g_TargetProcess;
static const GUID g_PwaDeviceClassGuid =
    { 0x9c9f7e31, 0x5f18, 0x4a66, { 0x9b, 0x2d, 0x19, 0x7e, 0x42, 0x50, 0x61, 0x77 } };
static UNICODE_STRING g_DefaultSecurity =
    RTL_CONSTANT_STRING(L"D:P(A;;GA;;;SY)(A;;GA;;;BA)");

static BOOLEAN PwaIsPvpAlive(_In_ PUNICODE_STRING FullImageName)
{
    // FullImageName is normally an NT path. Match the complete expected
    // installation-relative path rather than only the basename.
    UNICODE_STRING suffix = RTL_CONSTANT_STRING(
        L"\\Program Files (x86)\\perfectworldarena\\plugin\\PvpAlive.dll");
    return FullImageName != NULL && RtlSuffixUnicodeString(&suffix, FullImageName, TRUE);
}

static VOID PwaImageLoadNotify(
    _In_opt_ PUNICODE_STRING FullImageName,
    _In_ HANDLE ProcessId,
    _In_ PIMAGE_INFO ImageInfo)
{
    ULONG chars;
    PEPROCESS process = NULL;
    PEPROCESS previous = NULL;

    if (ImageInfo == NULL || ProcessId == NULL ||
        ImageInfo->ImageSize == 0 || ImageInfo->ImageSize > MAXULONG ||
        !PwaIsPvpAlive(FullImageName)) {
        return;
    }

    if (!NT_SUCCESS(PsLookupProcessByProcessId(ProcessId, &process))) {
        return;
    }

    ExAcquireFastMutex(&g_TargetLock);
    previous = g_TargetProcess;
    g_TargetProcess = process;
    RtlZeroMemory(&g_Target, sizeof(g_Target));
    g_Target.ProcessId = HandleToUlong(ProcessId);
    g_Target.ImageBase = (ULONGLONG)(ULONG_PTR)ImageInfo->ImageBase;
    g_Target.ImageSize = (ULONG)ImageInfo->ImageSize;
    g_Target.ProcessStartKey = PsGetProcessStartKey(process);
    if (FullImageName != NULL && FullImageName->Buffer != NULL) {
        chars = FullImageName->Length / sizeof(WCHAR);
        if (chars >= PWA_MAX_IMAGE_PATH) {
            chars = PWA_MAX_IMAGE_PATH - 1;
        }
        RtlCopyMemory(g_Target.ImagePath, FullImageName->Buffer, chars * sizeof(WCHAR));
        g_Target.ImagePath[chars] = L'\0';
    }
    g_TargetValid = TRUE;
    ExReleaseFastMutex(&g_TargetLock);

    if (previous != NULL) {
        ObDereferenceObject(previous);
    }
}

static VOID PwaProcessNotify(
    _In_ PEPROCESS Process,
    _In_ HANDLE ProcessId,
    _Inout_opt_ PPS_CREATE_NOTIFY_INFO CreateInfo)
{
    PEPROCESS stale = NULL;

    UNREFERENCED_PARAMETER(ProcessId);
    if (CreateInfo != NULL) {
        return;
    }

    ExAcquireFastMutex(&g_TargetLock);
    if (g_TargetProcess == Process) {
        stale = g_TargetProcess;
        g_TargetProcess = NULL;
        g_TargetValid = FALSE;
        RtlZeroMemory(&g_Target, sizeof(g_Target));
    }
    ExReleaseFastMutex(&g_TargetLock);

    if (stale != NULL) {
        ObDereferenceObject(stale);
    }
}

static NTSTATUS PwaCreateClose(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    UNREFERENCED_PARAMETER(DeviceObject);
    Irp->IoStatus.Status = STATUS_SUCCESS;
    Irp->IoStatus.Information = 0;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return STATUS_SUCCESS;
}

static NTSTATUS PwaReadProcessMemory(
    _In_ PEPROCESS Process,
    _In_ PVOID Address,
    _Out_writes_bytes_(Length) PVOID Buffer,
    _In_ SIZE_T Length)
{
    SIZE_T copied = 0;
    NTSTATUS status = MmCopyVirtualMemory(
        Process,
        Address,
        PsGetCurrentProcess(),
        Buffer,
        Length,
        KernelMode,
        &copied);
    return NT_SUCCESS(status) && copied == Length ? STATUS_SUCCESS : STATUS_PARTIAL_COPY;
}

static NTSTATUS PwaWriteProcessMemory(
    _In_ PEPROCESS Process,
    _In_ PVOID Address,
    _In_reads_bytes_(Length) PVOID Buffer,
    _In_ SIZE_T Length)
{
    KAPC_STATE apc;
    PMDL mdl = NULL;
    PVOID mapped = NULL;
    NTSTATUS status = STATUS_SUCCESS;
    BOOLEAN pagesLocked = FALSE;

    KeStackAttachProcess(Process, &apc);
    __try {
        mdl = IoAllocateMdl(Address, (ULONG)Length, FALSE, FALSE, NULL);
        if (mdl == NULL) {
            status = STATUS_INSUFFICIENT_RESOURCES;
            __leave;
        }
        // Executable image pages are commonly RX. Probe for read access, pin
        // the physical pages, then change only the temporary kernel mapping.
        // IoModifyAccess would reject a normal read-only code section.
        MmProbeAndLockPages(mdl, UserMode, IoReadAccess);
        pagesLocked = TRUE;
        mapped = MmMapLockedPagesSpecifyCache(
            mdl, KernelMode, MmCached, NULL, FALSE, NormalPagePriority);
        if (mapped == NULL) {
            status = STATUS_INSUFFICIENT_RESOURCES;
            __leave;
        }
        status = MmProtectMdlSystemAddress(mdl, PAGE_EXECUTE_READWRITE);
        if (!NT_SUCCESS(status)) {
            __leave;
        }
        RtlCopyMemory(mapped, Buffer, Length);
        KeMemoryBarrier();
        status = MmProtectMdlSystemAddress(mdl, PAGE_EXECUTE_READ);
        if (!NT_SUCCESS(status)) {
            // The write happened, but leaving the temporary mapping writable is
            // not acceptable. Surface the failure so the caller attempts its
            // verified rollback path instead of treating the patch as complete.
            __leave;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        status = GetExceptionCode();
    }

    if (mapped != NULL) {
        MmUnmapLockedPages(mapped, mdl);
    }
    if (mdl != NULL && pagesLocked) {
        MmUnlockPages(mdl);
    }
    if (mdl != NULL) {
        IoFreeMdl(mdl);
    }
    KeUnstackDetachProcess(&apc);
    return status;
}

static NTSTATUS PwaFlushProcessInstructionCache(
    _In_ PEPROCESS Process,
    _In_ PVOID Address,
    _In_ ULONG Length)
{
    HANDLE processHandle = NULL;
    NTSTATUS status = ObOpenObjectByPointer(
        Process,
        OBJ_KERNEL_HANDLE,
        NULL,
        PWA_PROCESS_QUERY_INFORMATION | PWA_PROCESS_VM_OPERATION,
        *PsProcessType,
        KernelMode,
        &processHandle);
    if (!NT_SUCCESS(status)) {
        return status;
    }

    status = ZwFlushInstructionCache(processHandle, Address, Length);
    ZwClose(processHandle);
    return status;
}

static NTSTATUS PwaApplyPatch(_Inout_ PPWA_PATCH_REQUEST Request)
{
    PEPROCESS process = NULL;
    UCHAR current[PWA_MAX_PATCH_BYTES];
    UCHAR verify[PWA_MAX_PATCH_BYTES];
    PVOID address;
    NTSTATUS status;
    ULONGLONG targetBase;
    ULONG targetSize;

    if (Request->ProcessId == 0 || Request->Address == 0 || Request->ProcessStartKey == 0 ||
        Request->Length != 1 || Request->ExpectedLength != 1 ||
        Request->Replacement[0] != 0xC3 ||
        Request->ExpectedLength > Request->Length) {
        return STATUS_INVALID_PARAMETER;
    }

    ExAcquireFastMutex(&g_TargetLock);
    if (!g_TargetValid || Request->ProcessId != g_Target.ProcessId ||
        Request->ProcessStartKey != g_Target.ProcessStartKey) {
        ExReleaseFastMutex(&g_TargetLock);
        return STATUS_NOT_FOUND;
    }
    targetBase = g_Target.ImageBase;
    targetSize = g_Target.ImageSize;
    if (g_TargetProcess == NULL) {
        ExReleaseFastMutex(&g_TargetLock);
        return STATUS_NOT_FOUND;
    }
    ObReferenceObject(g_TargetProcess);
    process = g_TargetProcess;
    ExReleaseFastMutex(&g_TargetLock);
    if (Request->Address < targetBase ||
        Request->Address - targetBase > (ULONGLONG)targetSize ||
        Request->Length > (ULONGLONG)targetSize - (Request->Address - targetBase)) {
        ObDereferenceObject(process);
        return STATUS_ACCESS_DENIED;
    }

    status = PsAcquireProcessExitSynchronization(process);
    if (!NT_SUCCESS(status)) {
        ObDereferenceObject(process);
        return status;
    }

    address = (PVOID)(ULONG_PTR)Request->Address;
    RtlZeroMemory(current, sizeof(current));
    status = PwaReadProcessMemory(process, address, current, Request->Length);
    if (!NT_SUCCESS(status)) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return status;
    }

    RtlCopyMemory(Request->Original, current, Request->Length);
    if (RtlCompareMemory(current, Request->Replacement, Request->Length) == Request->Length) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return STATUS_SUCCESS;
    }
    if (Request->ExpectedLength != 0 &&
        RtlCompareMemory(current, Request->Expected, Request->ExpectedLength) != Request->ExpectedLength) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return PWA_STATUS_EXPECTED_MISMATCH;
    }

    status = PwaWriteProcessMemory(process, address, Request->Replacement, Request->Length);
    if (NT_SUCCESS(status)) {
        RtlZeroMemory(verify, sizeof(verify));
        status = PwaReadProcessMemory(process, address, verify, Request->Length);
        if (NT_SUCCESS(status) &&
            RtlCompareMemory(verify, Request->Replacement, Request->Length) != Request->Length) {
            status = STATUS_DATA_ERROR;
        }
    }
    if (NT_SUCCESS(status)) {
        status = PwaFlushProcessInstructionCache(process, address, Request->Length);
    }
    if (!NT_SUCCESS(status)) {
        PwaWriteProcessMemory(process, address, current, Request->Length);
        PwaFlushProcessInstructionCache(process, address, Request->Length);
    }
    PsReleaseProcessExitSynchronization(process);
    ObDereferenceObject(process);
    return status;
}

static NTSTATUS PwaRestorePatch(_Inout_ PPWA_PATCH_REQUEST Request)
{
    PEPROCESS process = NULL;
    UCHAR current[PWA_MAX_PATCH_BYTES];
    UCHAR verify[PWA_MAX_PATCH_BYTES];
    PVOID address;
    NTSTATUS status;
    ULONGLONG targetBase;
    ULONG targetSize;

    if (Request->ProcessId == 0 || Request->Address == 0 || Request->ProcessStartKey == 0 ||
        Request->Length != 1 || Request->ExpectedLength != 1 ||
        Request->ExpectedLength > Request->Length) {
        return STATUS_INVALID_PARAMETER;
    }

    ExAcquireFastMutex(&g_TargetLock);
    if (!g_TargetValid || Request->ProcessId != g_Target.ProcessId ||
        Request->ProcessStartKey != g_Target.ProcessStartKey) {
        ExReleaseFastMutex(&g_TargetLock);
        return STATUS_NOT_FOUND;
    }
    targetBase = g_Target.ImageBase;
    targetSize = g_Target.ImageSize;
    if (g_TargetProcess == NULL) {
        ExReleaseFastMutex(&g_TargetLock);
        return STATUS_NOT_FOUND;
    }
    ObReferenceObject(g_TargetProcess);
    process = g_TargetProcess;
    ExReleaseFastMutex(&g_TargetLock);
    if (Request->Address < targetBase ||
        Request->Address - targetBase > (ULONGLONG)targetSize ||
        Request->Length > (ULONGLONG)targetSize - (Request->Address - targetBase)) {
        ObDereferenceObject(process);
        return STATUS_ACCESS_DENIED;
    }

    status = PsAcquireProcessExitSynchronization(process);
    if (!NT_SUCCESS(status)) {
        ObDereferenceObject(process);
        return status;
    }

    address = (PVOID)(ULONG_PTR)Request->Address;
    RtlZeroMemory(current, sizeof(current));
    status = PwaReadProcessMemory(process, address, current, Request->Length);
    if (!NT_SUCCESS(status)) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return status;
    }

    if (RtlCompareMemory(current, Request->Original, Request->Length) == Request->Length) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return STATUS_SUCCESS;
    }
    if (Request->ExpectedLength != 0 &&
        RtlCompareMemory(current, Request->Expected, Request->ExpectedLength) != Request->ExpectedLength) {
        PsReleaseProcessExitSynchronization(process);
        ObDereferenceObject(process);
        return PWA_STATUS_EXPECTED_MISMATCH;
    }

    status = PwaWriteProcessMemory(process, address, Request->Original, Request->Length);
    if (NT_SUCCESS(status)) {
        RtlZeroMemory(verify, sizeof(verify));
        status = PwaReadProcessMemory(process, address, verify, Request->Length);
        if (NT_SUCCESS(status) &&
            RtlCompareMemory(verify, Request->Original, Request->Length) != Request->Length) {
            status = STATUS_DATA_ERROR;
        }
    }
    if (NT_SUCCESS(status)) {
        status = PwaFlushProcessInstructionCache(process, address, Request->Length);
    }
    if (!NT_SUCCESS(status)) {
        PwaWriteProcessMemory(process, address, current, Request->Length);
        PwaFlushProcessInstructionCache(process, address, Request->Length);
    }
    PsReleaseProcessExitSynchronization(process);
    ObDereferenceObject(process);
    return status;
}

static NTSTATUS PwaDeviceControl(_In_ PDEVICE_OBJECT DeviceObject, _Inout_ PIRP Irp)
{
    PIO_STACK_LOCATION stack = IoGetCurrentIrpStackLocation(Irp);
    ULONG code = stack->Parameters.DeviceIoControl.IoControlCode;
    ULONG inputLength = stack->Parameters.DeviceIoControl.InputBufferLength;
    ULONG outputLength = stack->Parameters.DeviceIoControl.OutputBufferLength;
    PVOID buffer = Irp->AssociatedIrp.SystemBuffer;
    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    ULONG_PTR information = 0;

    UNREFERENCED_PARAMETER(DeviceObject);
    if (code == IOCTL_PWA_QUERY_TARGET &&
        inputLength == 0 && outputLength >= sizeof(PWA_TARGET_INFO)) {
        ExAcquireFastMutex(&g_TargetLock);
        if (g_TargetValid) {
            RtlCopyMemory(buffer, &g_Target, sizeof(g_Target));
            status = STATUS_SUCCESS;
            information = sizeof(g_Target);
        } else {
            status = STATUS_NOT_FOUND;
        }
        ExReleaseFastMutex(&g_TargetLock);
    } else if ((code == IOCTL_PWA_APPLY_PATCH || code == IOCTL_PWA_RESTORE_PATCH) &&
               buffer != NULL &&
               inputLength == sizeof(PWA_PATCH_REQUEST) &&
               outputLength == sizeof(PWA_PATCH_REQUEST)) {
        status = code == IOCTL_PWA_APPLY_PATCH
            ? PwaApplyPatch((PPWA_PATCH_REQUEST)buffer)
            : PwaRestorePatch((PPWA_PATCH_REQUEST)buffer);
        if (NT_SUCCESS(status) || status == PWA_STATUS_ALREADY_PATCHED ||
            status == PWA_STATUS_ALREADY_RESTORED) {
            information = sizeof(PWA_PATCH_REQUEST);
        }
    }

    Irp->IoStatus.Status = status;
    Irp->IoStatus.Information = information;
    IoCompleteRequest(Irp, IO_NO_INCREMENT);
    return status;
}

static VOID PwaUnload(_In_ PDRIVER_OBJECT DriverObject)
{
    PEPROCESS process;

    PsRemoveLoadImageNotifyRoutine(PwaImageLoadNotify);
    PsSetCreateProcessNotifyRoutineEx(PwaProcessNotify, TRUE);
    ExAcquireFastMutex(&g_TargetLock);
    process = g_TargetProcess;
    g_TargetProcess = NULL;
    g_TargetValid = FALSE;
    ExReleaseFastMutex(&g_TargetLock);
    if (process != NULL) {
        ObDereferenceObject(process);
    }
    IoDeleteSymbolicLink(&g_DosName);
    if (DriverObject->DeviceObject != NULL) {
        IoDeleteDevice(DriverObject->DeviceObject);
    }
}

NTSTATUS DriverEntry(_In_ PDRIVER_OBJECT DriverObject, _In_ PUNICODE_STRING RegistryPath)
{
    PDEVICE_OBJECT deviceObject = NULL;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(RegistryPath);
    ExInitializeFastMutex(&g_TargetLock);
    RtlZeroMemory(&g_Target, sizeof(g_Target));
    g_TargetValid = FALSE;
    g_TargetProcess = NULL;

    status = IoCreateDeviceSecure(
        DriverObject, 0, &g_DeviceName, PWA_DEVICE_TYPE,
        FILE_DEVICE_SECURE_OPEN, FALSE, &g_DefaultSecurity,
        &g_PwaDeviceClassGuid, &deviceObject);
    if (!NT_SUCCESS(status)) {
        return status;
    }
    status = IoCreateSymbolicLink(&g_DosName, &g_DeviceName);
    if (!NT_SUCCESS(status)) {
        IoDeleteDevice(deviceObject);
        return status;
    }
    status = PsSetLoadImageNotifyRoutine(PwaImageLoadNotify);
    if (!NT_SUCCESS(status)) {
        IoDeleteSymbolicLink(&g_DosName);
        IoDeleteDevice(deviceObject);
        return status;
    }
    status = PsSetCreateProcessNotifyRoutineEx(PwaProcessNotify, FALSE);
    if (!NT_SUCCESS(status)) {
        PsRemoveLoadImageNotifyRoutine(PwaImageLoadNotify);
        IoDeleteSymbolicLink(&g_DosName);
        IoDeleteDevice(deviceObject);
        return status;
    }

    deviceObject->Flags |= DO_BUFFERED_IO;
    DriverObject->MajorFunction[IRP_MJ_CREATE] = PwaCreateClose;
    DriverObject->MajorFunction[IRP_MJ_CLOSE] = PwaCreateClose;
    DriverObject->MajorFunction[IRP_MJ_DEVICE_CONTROL] = PwaDeviceControl;
    DriverObject->DriverUnload = PwaUnload;
    deviceObject->Flags &= ~DO_DEVICE_INITIALIZING;
    return STATUS_SUCCESS;
}
