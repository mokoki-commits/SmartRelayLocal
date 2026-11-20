#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// Переносит ресурсы Windows (значок, версия, манифест) из управляемой сборки в apphost exe.
///
/// Зачем: при сборке на Windows SDK сам вызывает BeginUpdateResource и встраивает ресурсы
/// в apphost, а на Linux/macOS эта возможность недоступна (NETSDK1074) — exe остаётся без
/// значка и в Проводнике выглядит «безликим». Здесь то же самое делается вручную: секция
/// .rsrc из SmartRelay.dll дописывается в exe новой секцией, каталог ресурсов направляется
/// на неё, а абсолютные RVA в записях данных пересчитываются на новое смещение.
///
/// Если ресурсы в exe уже есть (сборка на Windows), задача ничего не меняет.
/// </summary>
public sealed class PatchAppHostResources : Task
{
    [Required]
    public string AppHostPath { get; set; } = string.Empty;

    [Required]
    public string ManagedAssemblyPath { get; set; } = string.Empty;

    public override bool Execute()
    {
        if (!File.Exists(AppHostPath) || !File.Exists(ManagedAssemblyPath))
        {
            return true;
        }

        byte[] host = File.ReadAllBytes(AppHostPath);
        byte[] assembly = File.ReadAllBytes(ManagedAssemblyPath);

        PeImage hostImage = PeImage.Parse(host);
        PeImage assemblyImage = PeImage.Parse(assembly);

        if (hostImage.ResourceRva != 0)
        {
            // сборка на Windows: SDK уже встроил ресурсы, трогать нечего
            return true;
        }

        Section? rsrc = assemblyImage.FindSection(".rsrc");
        if (rsrc == null)
        {
            Log.LogWarning($"SmartRelay: в {Path.GetFileName(ManagedAssemblyPath)} нет секции .rsrc — значок в exe не перенесён");
            return true;
        }

        byte[] blob = new byte[rsrc.SizeOfRawData];
        Array.Copy(assembly, rsrc.PointerToRawData, blob, 0, blob.Length);

        int lastEnd = 0;
        foreach (Section section in hostImage.Sections)
        {
            lastEnd = Math.Max(lastEnd, section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData));
        }

        int newVirtualAddress = Align(lastEnd, hostImage.SectionAlignment);
        int rawOffset = Align(host.Length, hostImage.FileAlignment);
        int rawSize = Align(blob.Length, hostImage.FileAlignment);

        if (hostImage.SectionTableOffset + 40 * (hostImage.NumberOfSections + 1) > hostImage.SizeOfHeaders)
        {
            Log.LogWarning("SmartRelay: в заголовках exe нет места под новую секцию — значок в exe не перенесён");
            return true;
        }

        if (rawOffset < host.Length)
        {
            Log.LogWarning("SmartRelay: размер exe не выровнен под новую секцию — значок в exe не перенесён");
            return true;
        }

        // записи данных ресурсов хранят абсолютный RVA, поэтому при переносе секции их сдвигаем
        int delta = newVirtualAddress - rsrc.VirtualAddress;
        int shiftedEntries = ShiftDataEntries(blob, delta);

        int headerOffset = hostImage.SectionTableOffset + 40 * hostImage.NumberOfSections;
        Encoding.ASCII.GetBytes(".rsrc").CopyTo(host, headerOffset);
        WriteUInt32(host, headerOffset + 8, (uint)rsrc.VirtualSize);    // VirtualSize
        WriteUInt32(host, headerOffset + 12, (uint)newVirtualAddress);  // VirtualAddress
        WriteUInt32(host, headerOffset + 16, (uint)rawSize);            // SizeOfRawData
        WriteUInt32(host, headerOffset + 20, (uint)rawOffset);          // PointerToRawData
        WriteUInt32(host, headerOffset + 36, 0x40000040);               // INITIALIZED_DATA | READ

        WriteUInt16(host, hostImage.PeOffset + 6, (ushort)(hostImage.NumberOfSections + 1));
        WriteUInt32(host, hostImage.OptionalHeaderOffset + 8,
            hostImage.SizeOfInitializedData + (uint)rawSize);
        WriteUInt32(host, hostImage.OptionalHeaderOffset + 56,
            (uint)Align(newVirtualAddress + rsrc.VirtualSize, hostImage.SectionAlignment));
        WriteUInt32(host, hostImage.DataDirectoryOffset + 16, (uint)newVirtualAddress);
        WriteUInt32(host, hostImage.DataDirectoryOffset + 20, (uint)rsrc.VirtualSize);

        byte[] patched = new byte[rawOffset + rawSize];
        Array.Copy(host, patched, host.Length);
        Array.Copy(blob, 0, patched, rawOffset, blob.Length);
        File.WriteAllBytes(AppHostPath, patched);

        Log.LogMessage(MessageImportance.High,
            $"SmartRelay: значок, версия и манифест перенесены в {Path.GetFileName(AppHostPath)} " +
            $"(.rsrc {rsrc.VirtualSize} байт, записей {shiftedEntries})");
        return true;
    }

    /// <summary>Каталог ресурсов: смещения внутри него относительны начала секции, а в записях данных — абсолютные RVA.</summary>
    private static int ShiftDataEntries(byte[] blob, int delta)
    {
        return blob.Length < 16 ? 0 : ShiftDataEntries(blob, 0, delta, 0);
    }

    private static int ShiftDataEntries(byte[] blob, int offset, int delta, int depth)
    {
        if (depth > 8 || offset + 16 > blob.Length)
        {
            return 0;
        }

        int named = BitConverter.ToUInt16(blob, offset + 12);
        int ids = BitConverter.ToUInt16(blob, offset + 14);
        int shifted = 0;

        for (int i = 0; i < named + ids; i++)
        {
            int entry = offset + 16 + i * 8;
            if (entry + 8 > blob.Length)
            {
                break;
            }

            uint target = BitConverter.ToUInt32(blob, entry + 4);
            if ((target & 0x80000000u) != 0)
            {
                shifted += ShiftDataEntries(blob, (int)(target & 0x7FFFFFFFu), delta, depth + 1);
            }
            else if ((long)target + 4 <= blob.Length)
            {
                uint rva = BitConverter.ToUInt32(blob, (int)target);
                WriteUInt32(blob, (int)target, rva + (uint)delta);
                shifted++;
            }
        }

        return shifted;
    }

    private static int Align(int value, int alignment)
    {
        return alignment <= 0 ? value : (value + alignment - 1) / alignment * alignment;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private sealed class Section
    {
        public Section(string name, int virtualSize, int virtualAddress, int sizeOfRawData, int pointerToRawData)
        {
            Name = name;
            VirtualSize = virtualSize;
            VirtualAddress = virtualAddress;
            SizeOfRawData = sizeOfRawData;
            PointerToRawData = pointerToRawData;
        }

        public string Name { get; }
        public int VirtualSize { get; }
        public int VirtualAddress { get; }
        public int SizeOfRawData { get; }
        public int PointerToRawData { get; }
    }

    /// <summary>Минимальный разбор PE-заголовков: только то, что нужно для добавления секции .rsrc.</summary>
    private sealed class PeImage
    {
        private readonly byte[] _bytes;

        private PeImage(byte[] bytes, int peOffset, int numberOfSections, int optionalHeaderOffset,
            int sectionTableOffset, int dataDirectoryOffset, int sectionAlignment, int fileAlignment,
            int sizeOfHeaders, uint sizeOfInitializedData, uint resourceRva)
        {
            _bytes = bytes;
            PeOffset = peOffset;
            NumberOfSections = numberOfSections;
            OptionalHeaderOffset = optionalHeaderOffset;
            SectionTableOffset = sectionTableOffset;
            DataDirectoryOffset = dataDirectoryOffset;
            SectionAlignment = sectionAlignment;
            FileAlignment = fileAlignment;
            SizeOfHeaders = sizeOfHeaders;
            SizeOfInitializedData = sizeOfInitializedData;
            ResourceRva = resourceRva;
        }

        public int PeOffset { get; }
        public int NumberOfSections { get; }
        public int OptionalHeaderOffset { get; }
        public int SectionTableOffset { get; }
        public int DataDirectoryOffset { get; }
        public int SectionAlignment { get; }
        public int FileAlignment { get; }
        public int SizeOfHeaders { get; }
        public uint SizeOfInitializedData { get; }
        public uint ResourceRva { get; }

        public static PeImage Parse(byte[] bytes)
        {
            if (bytes.Length < 0x40)
            {
                throw new InvalidDataException("Файл слишком мал для PE-образа");
            }

            int peOffset = BitConverter.ToInt32(bytes, 0x3C);
            if (peOffset <= 0 || peOffset + 24 > bytes.Length ||
                bytes[peOffset] != (byte)'P' || bytes[peOffset + 1] != (byte)'E')
            {
                throw new InvalidDataException("Не найден заголовок PE");
            }

            int optionalHeaderOffset = peOffset + 24;
            int dataDirectoryOffset = optionalHeaderOffset +
                (BitConverter.ToUInt16(bytes, optionalHeaderOffset) == 0x20B ? 112 : 96);

            return new PeImage(
                bytes,
                peOffset,
                BitConverter.ToUInt16(bytes, peOffset + 6),
                optionalHeaderOffset,
                optionalHeaderOffset + BitConverter.ToUInt16(bytes, peOffset + 20),
                dataDirectoryOffset,
                BitConverter.ToInt32(bytes, optionalHeaderOffset + 32),
                BitConverter.ToInt32(bytes, optionalHeaderOffset + 36),
                BitConverter.ToInt32(bytes, optionalHeaderOffset + 60),
                BitConverter.ToUInt32(bytes, optionalHeaderOffset + 8),
                BitConverter.ToUInt32(bytes, dataDirectoryOffset + 16));
        }

        public Section? FindSection(string name)
        {
            foreach (Section section in Sections)
            {
                if (string.Equals(section.Name, name, StringComparison.Ordinal))
                {
                    return section;
                }
            }

            return null;
        }

        public IEnumerable<Section> Sections
        {
            get
            {
                for (int i = 0; i < NumberOfSections; i++)
                {
                    int offset = SectionTableOffset + 40 * i;
                    if (offset + 40 > _bytes.Length)
                    {
                        yield break;
                    }

                    yield return new Section(
                        Encoding.ASCII.GetString(_bytes, offset, 8).TrimEnd('\0'),
                        BitConverter.ToInt32(_bytes, offset + 8),
                        BitConverter.ToInt32(_bytes, offset + 12),
                        BitConverter.ToInt32(_bytes, offset + 16),
                        BitConverter.ToInt32(_bytes, offset + 20));
                }
            }
        }
    }
}
