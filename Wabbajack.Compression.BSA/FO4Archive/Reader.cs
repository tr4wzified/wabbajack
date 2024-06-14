using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Wabbajack.Compression.BSA.Interfaces;
using Wabbajack.DTOs.BSA.ArchiveStates;
using Wabbajack.DTOs.Streams;

namespace Wabbajack.Compression.BSA.FO4Archive;

public class Reader : IReader
{
    private readonly Stream _stream;
    internal string _headerMagic;
    internal ulong _nameTableOffset;
    internal uint _numFiles;
    internal uint _unknown1;
    internal uint _unknown2;
    internal uint _compressionFlag;
    internal BinaryReader _rdr;
    public IStreamFactory _streamFactory;
    internal BA2EntryType _type;

    /// <summary>
    /// Fallout 4 - Version 1, 7 or 8
    /// Starfield - Version 2 or 3
    /// </summary>
    internal uint _version;

    private Reader(Stream stream)
    {
        _stream = stream;
        _rdr = new BinaryReader(_stream, Encoding.UTF8);
    }

    public bool UseATIFourCC { get; set; } = false;

    public bool HasNameTable => _nameTableOffset > 0;

    public IEnumerable<IFile> Files { get; private set; }

    public IArchive State => new BA2State
    {
        Version = _version,
        HeaderMagic = _headerMagic,
        Type = _type,
        HasNameTable = HasNameTable
    };


    public static async Task<Reader> Load(IStreamFactory streamFactory)
    {
        var rdr = new Reader(await streamFactory.GetStream()) {_streamFactory = streamFactory};
        await rdr.LoadHeaders();
        return rdr;
    }

    private Task LoadHeaders()
    {
        _headerMagic = Encoding.ASCII.GetString(_rdr.ReadBytes(4));

        if (_headerMagic != "BTDX")
            throw new InvalidDataException("Unknown header type: " + _headerMagic);

        _version = _rdr.ReadUInt32();

        var fourcc = Encoding.ASCII.GetString(_rdr.ReadBytes(4));

        if (Enum.TryParse(fourcc, out BA2EntryType entryType))
            _type = entryType;
        else
            throw new InvalidDataException($"Can't parse entry types of {fourcc}");

        _numFiles = _rdr.ReadUInt32();
        _nameTableOffset = _rdr.ReadUInt64();

        _unknown1 = (_version >= 2) ? _rdr.ReadUInt32() : 0;
        _unknown2 = (_version >= 2) ? _rdr.ReadUInt32() : 0;
        _compressionFlag = (_version >= 3) ? _rdr.ReadUInt32() : 0;

        var files = new List<IBA2FileEntry>();
        for (var idx = 0; idx < _numFiles; idx += 1)
            switch (_type)
            {
                case BA2EntryType.GNRL:
                    files.Add(new FileEntry(this, idx));
                    break;
                case BA2EntryType.DX10:
                    if (_version == 2 || _version == 3)
                        files.Add(new SFArchive.DX10Entry(this, idx, _compressionFlag == 3));
                    else
                        files.Add(new DX10Entry(this, idx));
                    break;
                case BA2EntryType.GNMF:
                    break;
            }

        if (HasNameTable)
        {
            _rdr.BaseStream.Seek((long) _nameTableOffset, SeekOrigin.Begin);
            foreach (var file in files)
                file.FullPath = Encoding.UTF8.GetString(_rdr.ReadBytes(_rdr.ReadInt16()));
        }

        Files = files;
        _stream?.Dispose();
        _rdr.Dispose();

        return Task.CompletedTask;
    }
}
