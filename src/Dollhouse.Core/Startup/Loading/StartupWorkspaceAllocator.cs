using System.ComponentModel;

namespace Dollhouse.Core.Startup;

/// <summary>A bounded address-space walk, with OS failures preserved instead of timed retries.</summary>
internal static class StartupWorkspaceAllocator
{
    internal readonly record struct Reservation(nint Address, int Error);

    internal static int Allocate(nint imageBase, int imageSize, int granularity,
        Func<nint, StartupMemoryRegion> query, Func<nint, int, Reservation> reserve)
    {
        if (imageSize <= 0 || granularity < StartupWorkspace.PageSize || (granularity & (granularity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(granularity), "Invalid image size or allocation granularity.");
        long basis = imageBase.ToInt64();
        long first = Align(checked(basis + imageSize));
        long limit = checked(basis + int.MaxValue - StartupWorkspace.TotalSize);
        int regions = 0, attempts = 0, lastError = 0;
        long candidate = first;
        try
        {
            while (candidate <= limit)
            {
                var region = query((nint)candidate);
                regions++;
                long end = checked(region.Base.ToInt64() + (long)region.Size);
                if (region.Base.ToInt64() > candidate || end <= candidate)
                    throw new IOException("VirtualQueryEx returned a non-progressing region.");
                if (region.State == 0x10000 && candidate + StartupWorkspace.TotalSize <= end)
                {
                    attempts++;
                    var result = reserve((nint)candidate, StartupWorkspace.TotalSize);
                    if (result.Address != 0)
                    {
                        if (result.Address.ToInt64() != candidate)
                            throw new IOException("VirtualAllocEx returned an unexpected workspace address.");
                        return checked((int)(result.Address.ToInt64() - basis));
                    }
                    lastError = result.Error;
                    candidate = checked(candidate + granularity); // Reservation lost to another allocation; query the next boundary.
                }
                else candidate = Align(end);
            }
        }
        catch (Exception error) when (error is Win32Exception or IOException or OverflowException)
        {
            throw Failure(error);
        }
        throw Failure(null);

        long Align(long address) => checked(address + granularity - 1) & -(long)granularity;
        IOException Failure(Exception? cause) => new(
            $"Cannot reserve startup workspace before resuming the child: bytes={StartupWorkspace.TotalSize}, range=0x{first:x}..0x{limit:x}, candidate=0x{candidate:x}, regions={regions}, allocationAttempts={attempts}, lastAllocationError={lastError}" +
            (cause is Win32Exception win32 ? $", queryError={win32.NativeErrorCode}." : ".") +
            (cause is null ? "" : " " + cause.Message), cause);
    }
}
