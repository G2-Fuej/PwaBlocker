#pragma once

#include <ntifs.h>

// This kernel export is not declared by the public WDK headers used by the
// selected kit version. The import is provided by ntoskrnl.lib.
NTSTATUS NTAPI MmCopyVirtualMemory(
    _In_ PEPROCESS FromProcess,
    _In_ PVOID FromAddress,
    _In_ PEPROCESS ToProcess,
    _Out_ PVOID ToAddress,
    _In_ SIZE_T BufferSize,
    _In_ KPROCESSOR_MODE PreviousMode,
    _Out_ PSIZE_T NumberOfBytesCopied);

NTSYSAPI NTSTATUS NTAPI ZwFlushInstructionCache(
    _In_ HANDLE ProcessHandle,
    _In_opt_ PVOID BaseAddress,
    _In_ ULONG NumberOfBytesToFlush);

NTSTATUS NTAPI PsAcquireProcessExitSynchronization(_In_ PEPROCESS Process);
VOID NTAPI PsReleaseProcessExitSynchronization(_In_ PEPROCESS Process);

#define PWA_DEVICE_TYPE 0x8000
#define PWA_IOCTL_BASE 0x800
#define PWA_MAX_PATCH_BYTES 32
#define PWA_MAX_IMAGE_PATH 260
#define PWA_PROCESS_QUERY_INFORMATION 0x0400
#define PWA_PROCESS_VM_OPERATION 0x0008

#define IOCTL_PWA_QUERY_TARGET CTL_CODE(PWA_DEVICE_TYPE, PWA_IOCTL_BASE + 1, METHOD_BUFFERED, FILE_READ_ACCESS)
#define IOCTL_PWA_APPLY_PATCH CTL_CODE(PWA_DEVICE_TYPE, PWA_IOCTL_BASE + 2, METHOD_BUFFERED, FILE_WRITE_ACCESS)
#define IOCTL_PWA_RESTORE_PATCH CTL_CODE(PWA_DEVICE_TYPE, PWA_IOCTL_BASE + 3, METHOD_BUFFERED, FILE_WRITE_ACCESS)

#define PWA_STATUS_ALREADY_PATCHED ((NTSTATUS)0xE0001001L)
#define PWA_STATUS_EXPECTED_MISMATCH ((NTSTATUS)0xE0001002L)
#define PWA_STATUS_ALREADY_RESTORED ((NTSTATUS)0xE0001003L)

#pragma pack(push, 1)

typedef struct _PWA_TARGET_INFO {
    ULONG ProcessId;
    ULONG ImageSize;
    ULONGLONG ImageBase;
    ULONGLONG ProcessStartKey;
    WCHAR ImagePath[PWA_MAX_IMAGE_PATH];
} PWA_TARGET_INFO, *PPWA_TARGET_INFO;

typedef struct _PWA_PATCH_REQUEST {
    ULONG ProcessId;
    ULONG Length;
    ULONGLONG Address;
    ULONGLONG ProcessStartKey;
    ULONG ExpectedLength;
    UCHAR Expected[PWA_MAX_PATCH_BYTES];
    UCHAR Replacement[PWA_MAX_PATCH_BYTES];
    UCHAR Original[PWA_MAX_PATCH_BYTES];
} PWA_PATCH_REQUEST, *PPWA_PATCH_REQUEST;

#pragma pack(pop)

C_ASSERT(sizeof(PWA_TARGET_INFO) == 544);
C_ASSERT(sizeof(PWA_PATCH_REQUEST) == 124);
