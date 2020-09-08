using System;
using System.Collections.Generic;
using System.Text;

using System.IO;
using System.Linq;

namespace AtxInfo
{
    class AtxDisk
    {
        enum SectorStatus
        {
            FDC_DATAREQ_PENDING = 0x02,
            // Sector data exists but is incomplete
            FDC_LOSTDATA_ERROR = 0x04,
            // Sector data exists but is incorrect
            FDC_CRC_ERROR = 0x08,
            // No sector data available
            MISSING_DATA = 0x10,
            // Sector data exists but is marked as deleted
            DELETED = 0x20,
            // Sector has extended information chunk
            EXTENDED = 0x40
        };

        const int ATX_WEAKOFFSET_NONE = 0xFFFF;
        const int ANGULAR_UNIT_COUNT = 26042;

        enum CreatorId
        {
            ATX_CR_FX7 = 0x01,
            ATX_CR_FX8 = 0x02,
            ATX_CR_ATR = 0x03,
            ATX_CR_WH2PC = 0x10,
            ATX_CR_A8DISKUTILS = 0x74
        }
        enum ExtendedSize
        {
            EXTENDEDSIZE_128 = 0x00,
            EXTENDEDSIZE_256 = 0x01,
            EXTENDEDSIZE_512 = 0x02,
            EXTENDEDSIZE_1024 = 0x03
        }

        enum TrackFlags
        {
            MFM = 0x0002,
            UNKNOWN_SKEW = 0x0100
        }

        enum Density
        {
            SINGLE = 0x00,
            MEDIUM = 0x01,
            DOUBLE = 0x02
        }

        enum SPT
        {
            NORMAL = 18,
            ENHANCED = 26
        }
        enum SectorSize
        {
            NORMAL = 128,
            DOUBLE = 256
        }

        enum ChunkType
        {
            SECTOR_DATA = 0x00,
            SECTOR_LIST = 0x01,
            WEAK_SECTOR = 0x10,
            EXTENDED_HEADER = 0x11
        }
        enum RecordType
        {
            TRACK = 0x0000,
            HOST = 0x0100
        }

        public struct atx_header
        {
            public char[] magic;
            public UInt16 version;
            public UInt16 min_version;
            public UInt16 creator;
            public UInt16 creator_version;
            public UInt32 flags;
            public UInt16 image_type;
            public Byte density;
            public Byte reserved1;
            public UInt32 image_id;
            public UInt16 image_version;
            public UInt16 reserved2;
            public UInt32 start;
            public UInt32 end;
        };
        const int atx_header_bytecount = 36;

        public struct record_header
        {
            public UInt32 length;
            public UInt16 type;
            public UInt16 reserved;
        };
        const int record_header_bytecount = 8;

        public struct track_header
        {
            public Byte track_number;
            public Byte reserved1;
            public UInt16 sector_count;
            public UInt16 rate;
            public UInt16 reserved2;
            public UInt32 flags;
            public UInt32 header_size;
            public UInt64 reserved3;
        };
        const int track_header_bytecount = 24;

        struct chunk_header
        {
            public UInt32 length;
            public Byte type;
            public Byte sector_index;
            public UInt16 header_data;
        };
        const int chunk_header_bytecount = 8;

        class AtxSector
        {
            // 1-based and possible to have duplicates    
            public byte number;
            // ATX_SECTOR_STATUS bit flags     
            public byte status;
            // 0-based starting angular position of sector in 8us intervals (1/26042th of a rotation or ~0.0138238 degrees). Nominally 0-26042    
            public UInt16 position;
            // Byte offset from start of track data record to first byte of sector data within the sector data chunk. No data is present when sector status bit 4 set
            public UInt32 start_data;

            // Byte offset within sector at which weak (random) data should be returned    
            public UInt16 weakoffset = ATX_WEAKOFFSET_NONE;
            // Physical size of long sector (one of ATX_EXTENDESIZE)
            public UInt16 extendedsize;
        };

        const int sector_header_bytecount = 8;

        class AtxTrack
        {

            // Number of physical sectors in track
            public UInt16 sector_count = 0;
            // ? unknown use ?
            public UInt16 rate;
            // ATX_TRACK_FLAGS bit flags
            public UInt32 flags;

            public int track_number;

            // Keep count of bytes read into ATX track record
            public int record_bytes_read = 0;
            public int offset_to_data_start = 0;

            // Actual sector data
            public byte[] data;

            // Actual sectors
            public List<AtxSector> sectors = new List<AtxSector>();
        };

        atx_header _header;

        int _record_count = 0;
        int _sectors_per_track = (int)SPT.NORMAL;
        int _sector_size = (int)SectorSize.NORMAL;
        int _sector_data_bytes = 0;
        bool _verbose = false;

        List<AtxTrack> _tracks = new List<AtxTrack>();

        /*
         * Read EXTENDED_DATA CHUNK type
         */
        bool _load_extended_sector_chunk(chunk_header chunkhdr, AtxTrack track, BinaryReader reader)
        {
            if (chunkhdr.length != chunk_header_bytecount)
            {
                Console.WriteLine($"\tERROR:: Chunk length {chunkhdr.length} != expected ({chunk_header_bytecount})");
            }

            if (chunkhdr.sector_index >= track.sector_count)
            {
                Console.WriteLine("\tERROR:: Extended sector index > track sector count");
                return false;
            }

            UInt16 xsize;
            switch ((ExtendedSize)chunkhdr.header_data)
            {
                case ExtendedSize.EXTENDEDSIZE_128:
                    xsize = 128;
                    break;
                case ExtendedSize.EXTENDEDSIZE_256:
                    xsize = 256;
                    break;
                case ExtendedSize.EXTENDEDSIZE_512:
                    xsize = 512;
                    break;
                case ExtendedSize.EXTENDEDSIZE_1024:
                    xsize = 1024;
                    break;
                default:
                    Console.WriteLine($"\tERROR:: Invalid extended sector value {chunkhdr.header_data}");
                    return false;
            }

            track.sectors[chunkhdr.sector_index].extendedsize = xsize;

            int sector_number = track.sectors[chunkhdr.sector_index].number;
            int overall_sector_number = track.track_number * _sectors_per_track + sector_number;
            Console.WriteLine($"\tExtended sector: index={chunkhdr.sector_index}, num={sector_number} ({overall_sector_number}, ${overall_sector_number:x3}), size={xsize}");

            return true;
        }

        /*
         * Read WEAK_SECTOR CHUNK type
         */
        bool _load_weak_sector_chunk(chunk_header chunkhdr, AtxTrack track, BinaryReader reader)
        {
            if(chunkhdr.length != chunk_header_bytecount)
            {
                Console.WriteLine($"\tERROR:: Chunk length {chunkhdr.length} != expected ({chunk_header_bytecount})");
            }
            if(chunkhdr.sector_index >= track.sector_count)
            {
                Console.WriteLine("\tERROR:: Weak sector index > track sector count");
                return false;
            }

            track.sectors[chunkhdr.sector_index].weakoffset = chunkhdr.header_data;

            int sector_number = track.sectors[chunkhdr.sector_index].number;
            int overall_sector_number = track.track_number * _sectors_per_track + sector_number;
            Console.WriteLine($"\tWeak sector: index={chunkhdr.sector_index}, num={sector_number} ({overall_sector_number}, ${overall_sector_number:x3}), offset={chunkhdr.header_data}");

            return true;
        }

        /*
         * Read SECTOR_DATA CHUNK type
         */
        bool _load_sector_data_chunk(chunk_header chunkhdr, AtxTrack track, BinaryReader reader)
        {
            int data_size = (int)chunkhdr.length - chunk_header_bytecount;

            if (track.sector_count > 0 && track.sectors.Any() == false)
            {
                Console.WriteLine($"\tWARNING:: SECTOR_DATA chunk presented before SECTOR_LIST chunk");
            }
            else
            {
                int actual_sectors = 0;
                string missing_sectors = "";
                foreach(var s in track.sectors)
                {
                    if ((s.status & (byte)SectorStatus.MISSING_DATA) == 0)
                        actual_sectors++;
                    else
                        missing_sectors = "; missing sectors accounted for";
                }
                int calculated_bytes = actual_sectors * _sector_size;

                if (data_size != calculated_bytes)
                    Console.WriteLine($"\tWARNING:: Chunk data size as given in header ({data_size:N0}) != expected size ({actual_sectors} * {_sector_size} = {calculated_bytes:N0}){missing_sectors}");
            }

            Console.WriteLine($"\tReading {data_size:N0} bytes of track sector data");

            try
            {
                track.data = reader.ReadBytes(data_size);
            }
            catch
            {
                Console.WriteLine($"\tERROR:: Failed to read sector data");
                return false;
            }

            /*
            The start_data value in each sector header is an offset into the overall Track Record,
            including headers and other chunks that preceed it, where that sector's actual data begins
            in the data chunk.

            We record the number of bytes into the Track Record the data chunk begins so we
            can adjust the start_data value later when we want to read the right section from this
            array of bytes.
            */
            track.offset_to_data_start = track.record_bytes_read;
            // Keep a count of how many bytes we've read into the Track Record
            track.record_bytes_read += data_size;

            _sector_data_bytes += data_size;

            return true;
        }

        /*
         * Read SECTOR_LIST CHUNK type
         */
        bool _load_sector_list_chunk(chunk_header chunkhdr, AtxTrack track, BinaryReader reader)
        {
            int expected = chunk_header_bytecount + track.sector_count * sector_header_bytecount;
            if (chunkhdr.length != expected)
                Console.WriteLine($"\tWARNING:: Chunk length {chunkhdr.length} != expected ({expected})");

            // Try to read sector data for sector_count sectors
            for (int i = 0; i < track.sector_count; i++)
            {
                AtxSector sect = new AtxSector();
                try
                {
                    sect.number = reader.ReadByte();
                    sect.status = reader.ReadByte();
                    sect.position = reader.ReadUInt16();
                    sect.start_data = reader.ReadUInt32();

                    track.record_bytes_read += sector_header_bytecount;
                }
                catch
                {
                    Console.WriteLine($"\tERROR:: Failed to read sector list header for sector at index {i}");
                    return false;
                }

                int overall_sector_number = track.track_number * _sectors_per_track + sect.number;
                string overall = $"({overall_sector_number}, ${overall_sector_number:x3})";

                if (sect.status != 0)
                {
                    Console.Write($"\tSector index={i}, num={sect.number} {overall}, flags=");
                    if ((sect.status & (byte)SectorStatus.DELETED) != 0)
                        Console.Write("DELETED ");
                    if ((sect.status & (byte)SectorStatus.MISSING_DATA) != 0)
                        Console.Write("MISSING_DATA ");
                    if ((sect.status & (byte)SectorStatus.EXTENDED) != 0)
                        Console.Write("EXTENDED ");
                    if ((sect.status & (byte)SectorStatus.FDC_CRC_ERROR) != 0)
                        Console.Write("CRC_ERROR ");
                    if ((sect.status & (byte)SectorStatus.FDC_LOSTDATA_ERROR) != 0)
                        Console.Write("LOSTDATA_ERROR");
                    if ((sect.status & (byte)SectorStatus.FDC_DATAREQ_PENDING) != 0)
                        Console.Write("DATAREQ_PENDING");
                    Console.WriteLine();

                    if ((sect.status & ~(byte)(SectorStatus.DELETED | SectorStatus.MISSING_DATA |
                        SectorStatus.EXTENDED | SectorStatus.FDC_CRC_ERROR |
                        SectorStatus.FDC_DATAREQ_PENDING | SectorStatus.FDC_LOSTDATA_ERROR )) != 0)
                        Console.WriteLine($"\tWARNING:: Unknown sector status flag 0x{sect.status:X2}");
                }

                if(sect.number > _sectors_per_track)
                    Console.WriteLine($"\tWARNING:: Sector index={i}, number={sect.number} > {_sectors_per_track}");
                if(sect.number == 0)
                    Console.WriteLine($"\tWARNING:: Sector index={i} has sector #0");

                if (sect.position >= ANGULAR_UNIT_COUNT)
                    Console.WriteLine($"\tWARNING:: Sector index={i}, num={sect.number} {overall}, angular position {sect.position} > {ANGULAR_UNIT_COUNT - 1}");


                // See if this is a duplicate
                foreach (var s in track.sectors)
                {
                    if(s.number == sect.number)
                    {
                        Console.WriteLine($"\tDUPLICATE sector #{sect.number:D2} {overall}");
                        break;
                    }
                }

                // Add to the list
                track.sectors.Add(sect);
            }

            Console.WriteLine($"\tRead {track.sectors.Count} sector headers for track {track.track_number}");

            // Report on any missing sectors
            for(int i = 1; i <= _sectors_per_track; i++)
            {
                if(track.sectors.Find(x => x.number == i) == null)
                    Console.WriteLine($"\tMISSING sector #{i}");
            }

            return true;
        }

        /*
         * Read UNKNOWN CHUNK type
         */
        bool _load_unknown_chunk(chunk_header chunkhdr, BinaryReader reader)
        {
            Console.WriteLine($"WARNING:: Unknown chunk type");
            return true;
        }

        /*
         * Read a TRACK CHUNK
         * Expected types are SECTOR_DATA, SECTOR_LIST, WEAK_SECTOR, and EXTENDED_HEADER
         * 
         * Returns:
         *   0 = Ok
         *   1 = Done (reached terminator chunk)
         *  -1 = Error
         */
        int _load_track_chunk(track_header trkhdr, AtxTrack track, BinaryReader reader, int chunkcount)
        {
            chunk_header chunkhdr;
            try 
            {
                chunkhdr.length = reader.ReadUInt32();
                chunkhdr.type = reader.ReadByte();
                chunkhdr.sector_index = reader.ReadByte();
                chunkhdr.header_data = reader.ReadUInt16();
            }
            catch
            {
                Console.WriteLine("  ERROR:: Failed to read chunk header");
                return -1;
            };

            // Keep a count of how many bytes we've read into the Track Record
            track.record_bytes_read += chunk_header_bytecount;

            // Check for a terminating marker
            if (chunkhdr.length == 0)
            {
                Console.WriteLine("  Chunk terminator");
                return 1; // 1 = done
            }

            string chunktype;
            if (Enum.IsDefined(typeof(ChunkType), (int)chunkhdr.type))
                chunktype = Enum.GetName(typeof(ChunkType), (int)chunkhdr.type);
            else
                chunktype = $"UNKNOWN ({chunkhdr.type})";

            Console.WriteLine($"  Chunk #{chunkcount} type={chunktype}, size={chunkhdr.length}, secIndex={chunkhdr.sector_index}, hdrData=0x{chunkhdr.header_data:X4}");

            switch((ChunkType)chunkhdr.type)
            {
                case ChunkType.SECTOR_LIST:
                    if (false == _load_sector_list_chunk(chunkhdr, track, reader))
                        return -1;
                    break;
                case ChunkType.SECTOR_DATA:
                    if (false == _load_sector_data_chunk(chunkhdr, track, reader))
                        return -1;
                    break;
                case ChunkType.WEAK_SECTOR:
                    if (false == _load_weak_sector_chunk(chunkhdr, track, reader))
                        return -1;
                    break;
                case ChunkType.EXTENDED_HEADER:
                    if (false == _load_extended_sector_chunk(chunkhdr, track, reader))
                        return -1;
                    break;
                default:
                    if (false == _load_unknown_chunk(chunkhdr, reader))
                        return -1;
                    return -1;
            }

            return 0;
        }

        void _dump_bytes(byte[] buff)
        {
            int bytes_per_line = 16;
            for (int j = 0; j < buff.Length; j += bytes_per_line)
            {
                for (int k = 0; (k + j) < buff.Length && k < bytes_per_line; k++)
                    Console.Write($" {buff[k + j]:X2} ");
                Console.WriteLine();
            }
            Console.WriteLine();
        }

        /*
         * Parses a TRACK record type by reading its constitute CHUNKS
         */
        bool _load_track_record(UInt32 length, BinaryReader reader)
        {
            track_header trkhdr;

            try
            {
                trkhdr.track_number = reader.ReadByte();
                trkhdr.reserved1 = reader.ReadByte();
                trkhdr.sector_count = reader.ReadUInt16();
                trkhdr.rate = reader.ReadUInt16();
                trkhdr.reserved2 = reader.ReadUInt16();
                trkhdr.flags = reader.ReadUInt32();
                trkhdr.header_size = reader.ReadUInt32();
                trkhdr.reserved3 = reader.ReadUInt64();
            }
            catch
            {
                Console.WriteLine("ERROR:: Failed to read track header");
                return false;
            }

            if (trkhdr.track_number != _tracks.Count)
                Console.WriteLine($"WARNING:: Expecting track #{_tracks.Count} but got #{trkhdr.track_number}");

            if (trkhdr.track_number >= 40)
                Console.WriteLine($"WARNING:: Track # greater than 40");

            // See if this track number already exists
            foreach(var t in _tracks)
            {   
                if(t.track_number == trkhdr.track_number)
                {
                    Console.WriteLine($"WARNING:: Track #{trkhdr.track_number} already exists");
                    break;
                }
            }

            AtxTrack track = new AtxTrack();
            track.track_number = trkhdr.track_number;
            track.sector_count = trkhdr.sector_count;
            track.rate = trkhdr.rate;
            track.flags = trkhdr.flags;
            _tracks.Add(track);

            Console.WriteLine($"Track #{track.track_number:D2}: sectors={track.sector_count}, rate={track.rate}");

            if (track.flags != 0)
            {
                Console.Write($"  Track flags: ");
                if ((track.flags & (uint)TrackFlags.MFM) == (uint)TrackFlags.MFM)
                    Console.Write("MFM ");
                if ((track.flags & (uint)TrackFlags.UNKNOWN_SKEW) == (uint)TrackFlags.UNKNOWN_SKEW)
                    Console.Write("SKEW_NOT_KNOWN");
                Console.WriteLine();

                if ((track.flags & ~((uint)TrackFlags.MFM | (uint)TrackFlags.UNKNOWN_SKEW)) != 0)
                    Console.WriteLine($"WARNING:: Unknown track flags 0x{track.flags:X8}");
            }

            if (_verbose && track.sector_count != _sectors_per_track)
                Console.WriteLine($"WARNING:: Track sector count ({track.sector_count}) != {_sectors_per_track}");

            // Keep a count of how many bytes we've read into the Track Record
            // So far we've read record_header + track_header bytes into this record
            track.record_bytes_read = record_header_bytecount + track_header_bytecount;

            // If needed, skip ahead to the first track chunk given the header size value
            // (The 'header_size' value includes both the current track header and the 'parent' record header)
            int chunk_start_offset = (int)trkhdr.header_size - record_header_bytecount - track_header_bytecount;
            if (chunk_start_offset > 0)
            {
                try
                {
                    reader.BaseStream.Seek(chunk_start_offset, SeekOrigin.Current);
                }
                catch
                {

                    Console.WriteLine($"ERROR:: Failed to seek {chunk_start_offset} bytes to first chunk in track record");
                    return false;
                }
                // Keep a count of how many bytes we've read into the Track Record
                track.record_bytes_read += chunk_start_offset;
            }

            // Read all the chunks in the track
            int i, j = 0;
            while ((i = _load_track_chunk(trkhdr, track, reader, ++j)) == 0) ;

            return i == 1; // Return FALSE on error condition
        }

        /*
         * Parses a HOST or OTHER record type by simply dumping its contents
         */
        bool _load_other_record(UInt32 length, UInt16 typeval, BinaryReader reader)
        {
            int recsize = (int)(length - record_header_bytecount);

            if ((RecordType)typeval == RecordType.HOST)
                Console.WriteLine($"NOTE: HOST record type ({recsize} bytes)");
            else
                Console.WriteLine($"WARNING:: UNKNOWN ({typeval}) record type ({recsize} bytes)");

            try
            {
                byte[] buffer = reader.ReadBytes(recsize);
                _dump_bytes(buffer);
            }
            catch
            {

                Console.WriteLine("ERROR:: Failed to seek past this record");
                return false;
            }

            return true;
        }

        /*
         * Loads a "record" from the ATX file. This can be either a TRACK or HOST record
         */
        bool _load_next_record(BinaryReader reader)
        {
            record_header rec;
            try
            {
                rec.length = reader.ReadUInt32();
                rec.type = reader.ReadUInt16();
                rec.reserved = reader.ReadUInt16();
                _record_count++;
            }
            catch (EndOfStreamException)
            {

                Console.WriteLine($"Reached EOF");
                return false;
            }
            catch
            {

                Console.WriteLine($"ERROR:: Failed to read header for record #{_record_count}");
                return false;
            }

            if ((RecordType)rec.type == RecordType.TRACK)
                return _load_track_record(rec.length, reader);
            else
                return _load_other_record(rec.length, rec.type, reader);
            
        }

        /*
         * Seek to the first record in the ATX file and keep loading records as long as they exist
         */
        bool _load_atx_data(BinaryReader reader)
        {
            // Seek to the start of ATX record data as specified in the header
            try
            {
                reader.BaseStream.Seek(_header.start, SeekOrigin.Begin);
            }
            catch
            {
                Console.WriteLine("ERROR:: Failed to seek to start of ATX record data");
                return false;
            }

            while (_load_next_record(reader)) ;

            if(_tracks.Count < 40)
                Console.WriteLine($"WARNING:: Track count {_tracks.Count} is less than 40");

            Console.WriteLine($"ATX data load complete. Records={_record_count}, Tracks={_tracks.Count}, Data Bytes={_sector_data_bytes:N0}");
            return true;
        }

        public bool load_info(string fpath)
        {
            Console.WriteLine();
            Console.WriteLine($"   File: {Path.GetFileName(fpath)}");

            // Check size of file
            FileInfo info = new FileInfo(fpath);

            // Get file header contents
            using (BinaryReader reader = new BinaryReader(File.Open(fpath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                _header.magic = reader.ReadChars(4);
                if (_header.magic.Take(4).SequenceEqual("AT8X") == false)
                {
                    Console.WriteLine("ERROR:: File missing AT8X header");
                    return false;
                }

                _header.version = reader.ReadUInt16();
                _header.min_version = reader.ReadUInt16();
                _header.creator = reader.ReadUInt16();
                _header.creator_version = reader.ReadUInt16();
                _header.flags = reader.ReadUInt32();
                _header.image_type = reader.ReadUInt16();
                _header.density = reader.ReadByte();
                _header.reserved1 = reader.ReadByte();
                _header.image_id = reader.ReadUInt32();
                _header.image_version = reader.ReadUInt16();
                _header.reserved2 = reader.ReadUInt16();
                _header.start = reader.ReadUInt32();
                _header.end = reader.ReadUInt32();

                _sectors_per_track = _header.density == (byte)Density.MEDIUM ? (int)SPT.ENHANCED : (int)SPT.NORMAL;
                _sector_size = _header.density == (byte)Density.DOUBLE ? (int)SectorSize.DOUBLE : (int)SectorSize.NORMAL;

                Console.WriteLine($"Version: {_header.version}; {_header.min_version}");

                string creator;
                if (Enum.IsDefined(typeof(CreatorId), (Int32)_header.creator))
                    creator = Enum.GetName(typeof(CreatorId), (Int32)_header.creator);
                else
                    creator = $"UNKNOWN 0x{_header.creator:X4}";
                Console.WriteLine($"Creator: {creator}; {_header.creator_version}");

                Console.WriteLine($"  Flags: 0x{_header.flags:X8}");
                Console.WriteLine($"   Type: {_header.image_type}");
                Console.WriteLine($"Density: {Enum.GetName(typeof(Density), (Int32)_header.density)}");
                Console.WriteLine($"     ID: 0x{_header.image_id:X8}; {_header.image_version}");
                Console.WriteLine($"  Start: {_header.start:N0}");
                Console.WriteLine($"    End: {_header.end:N0}");

                if (info.Length != _header.end)
                {
                    // Creator ATX_CR_WH2PC is known to add a HOST record at the end of the file and not include
                    // its length in the ATX header "end" field.
                    if((CreatorId)_header.creator == CreatorId.ATX_CR_WH2PC && info.Length == _header.end + 48)
                    {
                            Console.WriteLine($"NOTE: Creator ATX_CR_WH2PC does not include HOST record length in header 'end' field value");
                    }
                    else
                        Console.WriteLine($"WARNING:: Header end field {_header.end:N0} doesn't match file size {info.Length:N0} (diff={(_header.end - info.Length):N0})");
                }

                return _load_atx_data(reader);
            }

        }

        public AtxDisk(bool verbose = false)
        {
            _verbose = verbose;
        }

    }
}
