using Android.OS;
using Android.Systems;
using BtChat.Tun;
using Java.IO;

namespace BtChat;

public sealed class AndroidTunIo(FileDescriptor fd) : ITunIo
{
    public int Read(byte[] buffer)
    {
        try
        {
            var poll = new StructPollfd { Fd = fd, Events = (short)OsConstants.Pollin };
            Os.Poll(new[] { poll }, 500);
            var revents = poll.Revents;
            if ((revents & (short)(OsConstants.Pollerr | OsConstants.Pollhup | OsConstants.Pollnval)) != 0) return -1;
            if ((revents & (short)OsConstants.Pollin) == 0) return 0;
            return Os.Read(fd, buffer, 0, buffer.Length);
        }
        catch (ErrnoException ex)
        {
            if (ex.Errno == OsConstants.Eagain || ex.Errno == OsConstants.Eintr) return 0;
            return -1;
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Os.Write(fd, buffer, offset, count);
                return;
            }
            catch (ErrnoException ex) when (ex.Errno == OsConstants.Eagain || ex.Errno == OsConstants.Eintr)
            {
                var poll = new StructPollfd { Fd = fd, Events = (short)OsConstants.Pollout };
                Os.Poll(new[] { poll }, 50);
            }
        }
    }
}
