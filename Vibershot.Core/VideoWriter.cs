using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace Vibershot.Core;

// Portable Motion-JPEG AVI; frames stream to disk rather than accumulating in memory.
public sealed class VideoWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly List<(uint Offset, uint Size)> _index = new();
    private readonly long _frameCount, _streamCount, _moviSize, _moviStart;
    private readonly int _fps;
    private bool _disposed;
    public VideoWriter(string path, int width, int height, int fps = 10)
    {
        _fps = fps;
        _writer = new BinaryWriter(File.Create(path));
        Four("RIFF"); _writer.Write(0); Four("AVI ");
        Four("LIST"); _writer.Write(192); Four("hdrl");
        Four("avih"); _writer.Write(56); _writer.Write(1_000_000 / fps);
        _writer.Write(0); _writer.Write(0); _writer.Write(0x10);
        _frameCount = _writer.BaseStream.Position; _writer.Write(0);
        _writer.Write(0); _writer.Write(1); _writer.Write(0); _writer.Write(width); _writer.Write(height);
        for (int i = 0; i < 4; i++) _writer.Write(0);
        Four("LIST"); _writer.Write(116); Four("strl");
        Four("strh"); _writer.Write(56); Four("vids"); Four("MJPG");
        _writer.Write(0); _writer.Write((short)0); _writer.Write((short)0); _writer.Write(0);
        _writer.Write(1); _writer.Write(fps); _writer.Write(0);
        _streamCount = _writer.BaseStream.Position; _writer.Write(0);
        _writer.Write(0); _writer.Write(-1); _writer.Write(0);
        _writer.Write((short)0); _writer.Write((short)0); _writer.Write((short)width); _writer.Write((short)height);
        Four("strf"); _writer.Write(40); _writer.Write(40); _writer.Write(width); _writer.Write(height);
        _writer.Write((short)1); _writer.Write((short)24); Four("MJPG"); _writer.Write(width * height * 3);
        _writer.Write(0); _writer.Write(0); _writer.Write(0); _writer.Write(0);
        Four("LIST"); _moviSize = _writer.BaseStream.Position; _writer.Write(0);
        _moviStart = _writer.BaseStream.Position; Four("movi");
    }
    public void AddFrame(BitmapSource frame)
    {
        if (_writer.BaseStream.Position > 1_800_000_000) throw new IOException("Recording reached the 1.8 GB limit. Stop and start another recording.");
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var data = new MemoryStream(); encoder.Save(data);
        uint size = checked((uint)data.Length);
        _index.Add((checked((uint)(_writer.BaseStream.Position - _moviStart)), size));
        Four("00dc"); _writer.Write(size); _writer.Write(data.GetBuffer(), 0, (int)size);
        if ((size & 1) != 0) _writer.Write((byte)0);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            long end = _writer.BaseStream.Position;
            Four("idx1"); _writer.Write(_index.Count * 16);
            foreach (var frame in _index) { Four("00dc"); _writer.Write(0x10); _writer.Write(frame.Offset); _writer.Write(frame.Size); }
            long length = _writer.BaseStream.Position;
            Patch(4, checked((int)(length - 8))); Patch(_moviSize, checked((int)(end - _moviSize - 4)));
            Patch(_frameCount, _index.Count); Patch(_streamCount, _index.Count);
        }
        finally { _writer.Dispose(); }
    }
    private void Four(string text) => _writer.Write(Encoding.ASCII.GetBytes(text));
    private void Patch(long position, int value) { _writer.BaseStream.Position = position; _writer.Write(value); }
}
