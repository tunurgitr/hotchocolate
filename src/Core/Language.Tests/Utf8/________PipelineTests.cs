using System.Buffers;
using System;
using System.IO;
using System.Threading.Tasks;
using System.IO.Pipelines;

namespace TestMe
{
    public class Foo
    {
        async Task FillPipeAsync(Stream stream, PipeWriter writer)
        {
            const int minimumBufferSize = 512;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);

            while (true)
            {
                // Allocate at least 512 bytes from the PipeWriter
                Memory<byte> memory = writer.GetMemory(minimumBufferSize);
                try
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    // Tell the PipeWriter how much was read from the Socket
                    buffer.CopyTo(memory);
                    writer.Advance(bytesRead);
                }
                catch (Exception ex)
                {
                    // LogError(ex);
                    break;
                }

                // Make the data available to the PipeReader
                FlushResult result = await writer.FlushAsync();

                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Tell the PipeReader that there's no more data coming
            writer.Complete();
        }

        async Task ReadPipeAsync(PipeReader reader)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();

                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition position = default;

                int segmentPosition = 0;

                do
                {
                    position = buffer.Start;
                    if (buffer.TryGet(ref position, out ReadOnlyMemory<byte> segment, true))
                    {

                    }
                }
                while (true);

                // Tell the PipeReader how much of the buffer we have consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete
            reader.Complete();
        }
    }
}
