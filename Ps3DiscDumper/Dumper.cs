﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using IrdLibraryClient;
using IrdLibraryClient.IrdFormat;
using Ps3DiscDumper.DiscInfo;
using Ps3DiscDumper.DiscKeyProviders;
using Ps3DiscDumper.POCOs;
using Ps3DiscDumper.Sfb;
using Ps3DiscDumper.Sfo;
using Ps3DiscDumper.Utils;
using FileInfo = System.IO.FileInfo;

namespace Ps3DiscDumper;

public class Dumper: IDisposable
{
    public const string Version = "4.0.5";

    static Dumper() => Log.Info("PS3 Disc Dumper v" + Version);

    private static readonly Regex VersionParts = new(@"(?<ver>\d+(\.\d+){0,2})[ \-]*(?<pre>.*)", RegexOptions.Singleline | RegexOptions.ExplicitCapture);
    private static readonly Regex ScsiInfoParts = new(@"Host: .+$\s*Vendor: (?<vendor>.+?)\s* Model: (?<model>.+?)\s* Rev: (?<revision>.+)$\s*Type: \s*(?<type>.+?)\s* ANSI  ?SCSI revision: (?<scsi_rev>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture);
    private static readonly HashSet<char> InvalidChars = new(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()));
    private static readonly char[] MultilineSplit = {'\r', '\n'};
    private long currentSector;
    private static readonly IDiscKeyProvider[] DiscKeyProviders = {new IrdProvider(), new RedumpProvider(),};
    private static readonly Dictionary<string, HashSet<DiscKeyInfo>> AllKnownDiscKeys = new();
    private readonly HashSet<string> TestedDiscKeys = new();
    private byte[] discSfbData;
    private static readonly Dictionary<string, ImmutableArray<byte>> Detectors = new()
    {
        [@"\PS3_GAME\LICDIR\LIC.DAT"] = "PS3LICDA"u8.ToImmutableArray(),
        [@"\PS3_GAME\USRDIR\EBOOT.BIN"] = "SCE"u8.ToArray().Concat(new byte[] { 0, 0, 0, 0, 2 }).ToImmutableArray(),
    };
    private byte[] detectionSector;
    private ImmutableArray<byte> detectionBytesExpected;
    private byte[] sectorIV;
    private Stream driveStream;
    private static readonly byte[] Iso9660PrimaryVolumeDescriptorHeader = {0x01, 0x43, 0x44, 0x30, 0x30, 0x31, 0x01, 0x00};
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = new SnakeCasePolicy(),
        WriteIndented = true,
    };
    public static readonly NameValueCollection RegionMapping = new()
    {
        ["A"] = "ASIA",
        ["E"] = "EU",
        ["H"] = "HK",
        ["J"] = "JP",
        ["K"] = "KR",
        ["P"] = "JP",
        ["T"] = "JP",
        ["U"] = "US",
    };

    public ParamSfo ParamSfo { get; private set; }
    public string ProductCode { get; private set; }
    public string DiscVersion { get; private set; }
    public string Title { get; private set; }
    public string OutputDir { get; private set; }
    public char Drive { get; set; }
    public string SelectedPhysicalDevice { get; private set; }
    public string InputDevicePath { get; private set; }
    private List<FileRecord> filesystemStructure;
    private List<DirRecord> emptyDirStructure;
    private CDReader discReader;
    private HashSet<DiscKeyInfo> allMatchingKeys;
    public KeyType DiscKeyType { get; set; }
    public string DiscKeyFilename { get; set; }
    private Decrypter Decrypter { get; set; }
    public int TotalFileCount { get; private set; }
    public int CurrentFileNumber { get; private set; }
    public long TotalSectors { get; private set; }
    public long TotalFileSize { get; private set; }
    public long SectorSize { get; private set; }
    private List<string> DiscFilenames { get; set; }
    public List<(string filename, string error)> BrokenFiles { get; } = new();
    public HashSet<DiscKeyInfo> ValidatingDiscKeys { get; } = new();
    public CancellationTokenSource Cts { get; private set; }
    public bool? ValidationStatus { get; private set; }
    public bool IsIvInitialized => sectorIV?.Length == 16;

    public Dumper(CancellationTokenSource cancellationTokenSource)
    {
        Cts = cancellationTokenSource;
    }

    public long CurrentSector
    {
        get
        {
            var tmp = Decrypter?.SectorPosition;
            if (tmp == null)
                return currentSector;
            return currentSector = tmp.Value;
        }
    }

    [SupportedOSPlatform("Windows")]
    private List<string> EnumeratePhysicalDrivesWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new NotImplementedException("This should never happen, shut up msbuild");
            
        var physicalDrives = new List<string>();
#if !NATIVE
        try
        {
            using var physicalMediaQuery = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMedia");
            var drives = physicalMediaQuery.Get();
            foreach (var drive in drives)
            {
                if (drive.Properties["Tag"].Value is string tag
                    && tag.StartsWith(@"\\.\CDROM"))
                    physicalDrives.Add(tag);
            }
            using var cdromQuery = new ManagementObjectSearcher("SELECT * FROM Win32_CDROMDrive");
            drives = cdromQuery.Get();
            foreach (var drive in drives)
            {
                // Name and Caption are the same, so idk if they can be different
                Log.Info($"Found optical media drive {drive.Properties["Name"].Value} ({drive.Properties["Drive"].Value})");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to enumerate physical media drives using WMI");
            for (var i = 0; i < 32; i++)
                physicalDrives.Add($@"\\.\CDROM{i}");
        }
#else
            for (var i = 0; i < 32; i++)
                physicalDrives.Add($@"\\.\CDROM{i}");
#endif
        return physicalDrives;
    }

    [SupportedOSPlatform("Linux")]
    private List<string> EnumeratePhysicalDrivesLinux()
    {
        var cdInfo = "";
        try
        {
            cdInfo = File.ReadAllText("/proc/sys/dev/cdrom/info");
            if (File.Exists("/proc/scsi/scsi"))
            {
                var scsiInfo = File.ReadAllText("/proc/scsi/scsi");
                if (scsiInfo is { Length: > 0 })
                {
                    foreach (Match m in ScsiInfoParts.Matches(scsiInfo))
                    {
                        if (m.Groups["type"].Value is not "CD-ROM")
                            continue;

                        Log.Info($"Found optical media drive {m.Groups["vendor"].Value} {m.Groups["model"].Value}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log.Debug(e, e.Message);
        }
        var lines = cdInfo.Split(MultilineSplit, StringSplitOptions.RemoveEmptyEntries);
        return lines.Where(s => s.StartsWith("drive name:")).Select(l => Path.Combine("/dev", l.Split(':').Last().Trim())).Where(File.Exists)
            .Concat(IOEx.GetFilepaths("/dev", "sr*", SearchOption.TopDirectoryOnly))
            .Distinct()
            .ToList();

    }

    private string CheckDiscSfb(byte[] discSfbData)
    {
        var sfb = SfbReader.Parse(discSfbData);
        var flags = new HashSet<char>(sfb.KeyEntries.FirstOrDefault(e => e.Key == "HYBRID_FLAG")?.Value?.ToCharArray() ?? new char[0]);
        Log.Debug($"Disc flags: {string.Concat(flags)}");
        if (!flags.Contains('g'))
            Log.Warn("Disc is not a game disc");
        var titleId = sfb.KeyEntries.FirstOrDefault(e => e.Key == "TITLE_ID")?.Value;
        Log.Debug($"Disc product code: {titleId}");
        if (string.IsNullOrEmpty(titleId))
            Log.Warn("Disc product code is empty");
        else if (titleId.Length > 9)
            titleId = titleId.Substring(0, 4) + titleId.Substring(titleId.Length - 5, 5);
        return titleId;
    }

    private void CheckParamSfo(ParamSfo sfo)
    {
        Title = sfo.Items.FirstOrDefault(i => i.Key == "TITLE")?.StringValue?.Trim(' ', '\0');
        var titleParts = Title.Split(MultilineSplit, StringSplitOptions.RemoveEmptyEntries);
        if (titleParts.Length > 1)
            Title = string.Join(" ", titleParts);
        ProductCode = sfo.Items.FirstOrDefault(i => i.Key == "TITLE_ID")?.StringValue?.Trim(' ', '\0');
        DiscVersion = sfo.Items.FirstOrDefault(i => i.Key == "VERSION")?.StringValue?.Trim();
        var appVer = sfo.Items.FirstOrDefault(i => i.Key == "APP_VER")?.StringValue.Trim();

        Log.Info($"Game title: {Title}");
        Log.Info($"Game product code: {ProductCode}");
        Log.Info($"Game version: {DiscVersion}");
        Log.Info($"App version: {appVer}");
    }

    public void DetectDisc(string inDir = "", Func<Dumper, string> outputDirFormatter = null)
    {
        outputDirFormatter ??= d => PatternFormatter.Format(SettingsProvider.Settings.DumpNameTemplate, new()
        {
            [Patterns.ProductCode] = d.ProductCode,
            [Patterns.ProductCodeLetters] = d.ProductCode?[..4],
            [Patterns.ProductCodeNumbers] = d.ProductCode?[4..],
            [Patterns.Title] = d.Title,
            [Patterns.Region] = RegionMapping[d.ProductCode?[2..3] ?? ""],
        });
        string discSfbPath = null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom && d.IsReady);
            if (string.IsNullOrEmpty(inDir))
            {
                foreach (var drive in drives)
                {
                    discSfbPath = Path.Combine(drive.Name, "PS3_DISC.SFB");
                    if (!File.Exists(discSfbPath))
                        continue;

                    InputDevicePath = drive.Name;
                    Drive = drive.Name[0];
                    break;
                }
            }
            else
            {
                discSfbPath = Path.Combine(inDir, "PS3_DISC.SFB");
                if (File.Exists(discSfbPath))
                {
                    InputDevicePath = Path.GetPathRoot(discSfbPath);
                    Drive = discSfbPath[0];
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var mountList = inDir is { Length: > 0 }
                ? new[] { inDir }
                : DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom).Select(d => d.RootDirectory.FullName); 
            discSfbPath = mountList.SelectMany(mp => IOEx.GetFilepaths(mp, "PS3_DISC.SFB", 2)) .FirstOrDefault();
            if (!string.IsNullOrEmpty(discSfbPath))
                InputDevicePath = Path.GetDirectoryName(discSfbPath)!;
        }
        else
            throw new NotImplementedException("Current OS is not supported");

        if (string.IsNullOrEmpty(InputDevicePath) || string.IsNullOrEmpty(discSfbPath))
            throw new DriveNotFoundException("No valid PS3 disc was detected. Disc must be detected and mounted.");

        Log.Info("Selected disc: " + InputDevicePath);
        discSfbData = File.ReadAllBytes(discSfbPath);
        var titleId = CheckDiscSfb(discSfbData);
        var paramSfoPath = Path.Combine(InputDevicePath, "PS3_GAME", "PARAM.SFO");
        if (!File.Exists(paramSfoPath))
            throw new InvalidOperationException($"Specified folder is not a valid PS3 disc root (param.sfo is missing): {InputDevicePath}");

        using (var stream = File.Open(paramSfoPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ParamSfo = ParamSfo.ReadFrom(stream);
        CheckParamSfo(ParamSfo);
        if (titleId != ProductCode)
            Log.Warn($"Product codes in ps3_disc.sfb ({titleId}) and in param.sfo ({ProductCode}) do not match");

        // todo: maybe use discutils instead to read TOC as one block
        var files = IOEx.GetFilepaths(InputDevicePath, "*", SearchOption.AllDirectories);
        DiscFilenames = new();
        var totalFilesize = 0L;
        var rootLength = InputDevicePath.Length;
        foreach (var f in files)
        {
            try { totalFilesize += new FileInfo(f).Length; } catch { }
            DiscFilenames.Add(f.Substring(rootLength));
        }
        TotalFileSize = totalFilesize;
        TotalFileCount = DiscFilenames.Count;

        var path = new string(outputDirFormatter(this).ToCharArray().Where(c => !InvalidChars.Contains(c)).ToArray());
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var pathParts = path.Split(separators, StringSplitOptions.RemoveEmptyEntries).Select(p => p.TrimEnd('.'));
        OutputDir = string.Join(Path.DirectorySeparatorChar, pathParts);
        Log.Debug($"Dump folder name: {OutputDir}");
    }

    public async Task FindDiscKeyAsync(string discKeyCachePath)
    {
        // reload disc keys
        try
        {
            foreach (var keyProvider in DiscKeyProviders)
            {
                Log.Trace($"Getting keys from {keyProvider.GetType().Name}...");
                var newKeys = await keyProvider.EnumerateAsync(discKeyCachePath, ProductCode, Cts.Token).ConfigureAwait(false);
                Log.Trace($"Got {newKeys.Count} keys from {keyProvider.GetType().Name}");
                lock (AllKnownDiscKeys)
                {
                    foreach (var keyInfo in newKeys)
                    {
                        try
                        {
                            if (!AllKnownDiscKeys.TryGetValue(keyInfo.DecryptedKeyId, out var duplicates))
                                AllKnownDiscKeys[keyInfo.DecryptedKeyId] = duplicates = new();
                            duplicates.Add(keyInfo);
                        }
                        catch (Exception e)
                        {
                            Log.Error(e);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load disc keys");
        }
        // check if user provided something new since the last attempt
        var untestedKeys = new HashSet<string>();
        lock (AllKnownDiscKeys)
            untestedKeys.UnionWith(AllKnownDiscKeys.Keys);
        untestedKeys.ExceptWith(TestedDiscKeys);
        if (untestedKeys.Count == 0)
            throw new KeyNotFoundException("No valid disc decryption key was found");

        List<string> physicalDrives = new List<string>();
        Log.Trace("Trying to enumerate physical drives...");
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                physicalDrives = EnumeratePhysicalDrivesWindows();
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                physicalDrives = EnumeratePhysicalDrivesLinux();
            else
                throw new NotImplementedException("Current OS is not supported");
        }
        catch (Exception e)
        {
            Log.Error(e);
            throw;
        }
        Log.Debug($"Found {physicalDrives.Count} physical drives");

        if (physicalDrives.Count == 0)
            throw new InvalidOperationException("No optical drives were found");

        foreach (var drive in physicalDrives)
        {
            try
            {
                Log.Trace($"Checking physical drive {drive}...");
                await using var discStream = File.Open(drive, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                var tmpDiscReader = new CDReader(discStream, true, true);
                if (tmpDiscReader.FileExists("PS3_DISC.SFB"))
                {
                    Log.Trace("Found PS3_DISC.SFB, getting sector data...");
                    var discSfbInfo = tmpDiscReader.GetFileInfo("PS3_DISC.SFB");
                    if (discSfbInfo.Length == discSfbData.Length)
                    {
                        var buf = new byte[discSfbData.Length];
                        var sector = tmpDiscReader.PathToClusters(discSfbInfo.FullName).First().Offset;
                        Log.Trace($"PS3_DISC.SFB sector number is {sector}, reading content...");
                        discStream.Seek(sector * tmpDiscReader.ClusterSize, SeekOrigin.Begin);
                        discStream.ReadExact(buf, 0, buf.Length);
                        if (buf.SequenceEqual(discSfbData))
                        {
                            SelectedPhysicalDevice = drive;
                            break;
                        }
                        Log.Trace("SFB content check failed, skipping the drive");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug($"Skipping drive {drive}: {e.Message}");
            }
        }
        if (SelectedPhysicalDevice == null)
            throw new AccessViolationException("Direct disk access to the drive was denied");

        Log.Debug($"Selected physical drive {SelectedPhysicalDevice}");
        driveStream = File.Open(SelectedPhysicalDevice, FileMode.Open, FileAccess.Read, FileShare.Read);

        // find disc license file
        discReader = new(driveStream, true, true);
        FileRecord detectionRecord = null;
        var expectedBytes = ImmutableArray<byte>.Empty;
        try
        {
            foreach (var path in Detectors.Keys)
                if (discReader.FileExists(path))
                {
                    var clusterRange = discReader.PathToClusters(path);
                    var fileInfo = discReader.GetFileSystemInfo(path);
                    var recordInfo = new FileRecordInfo(fileInfo.CreationTimeUtc, fileInfo.LastWriteTimeUtc);
                    var parent = fileInfo.Parent;
                    var parentInfo = new DirRecord(parent.FullName.TrimStart('\\'), new(parent.CreationTimeUtc, parent.LastWriteTimeUtc));
                    detectionRecord = new(path, clusterRange.Min(r => r.Offset), discReader.GetFileLength(path), recordInfo, parentInfo);
                    expectedBytes = Detectors[path];
                    if (detectionRecord.Length == 0)
                        continue;
                        
                    Log.Debug($"Using {path} for disc key detection");
                    break;
                }
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        if (detectionRecord is null)
            throw new FileNotFoundException("Couldn't find a single disc key detection file, please report");

        if (Cts.IsCancellationRequested)
            return;

        SectorSize = discReader.ClusterSize;

        // select decryption key
        driveStream.Seek(detectionRecord.StartSector * discReader.ClusterSize, SeekOrigin.Begin);
        detectionSector = new byte[discReader.ClusterSize];
        detectionBytesExpected = expectedBytes;
        sectorIV = Decrypter.GetSectorIV(detectionRecord.StartSector);
        Log.Debug($"Initialized {nameof(sectorIV)} ({sectorIV?.Length * 8} bit) for sector {detectionRecord.StartSector}: {sectorIV?.ToHexString()}");
        driveStream.ReadExact(detectionSector, 0, detectionSector.Length);
        string discKey = null;
        try
        {
            var validKeys = untestedKeys.AsParallel().Where(k => !Cts.IsCancellationRequested && IsValidDiscKey(k)).Distinct().ToList();
            if (validKeys.Count > 1)
            {
                Log.Warn($"Expected only one valid decryption key, but found {validKeys.Count}:");
                foreach (var k in validKeys)
                {
                    Log.Debug($"\t{k}:");
                    var kiList = AllKnownDiscKeys[k];
                    foreach (var ki in kiList)
                        Log.Debug($"\t\t{ki.KeyType}: {Path.GetFileName(ki.FullPath)}");
                }
            }
            discKey = validKeys.FirstOrDefault();
        }
        catch (Exception e)
        {
            Log.Error(e);
        }
        if (discKey == null)
            throw new KeyNotFoundException("No valid disc decryption key was found");

        Log.Info($"Selected disc key: {discKey}, known key sources:");
        var keyInfoList = AllKnownDiscKeys[discKey];
        foreach (var ki in keyInfoList)
            Log.Debug($"\t{ki.KeyType}: {Path.GetFileName(ki.FullPath)}");

        if (Cts.IsCancellationRequested)
            return;

        lock (AllKnownDiscKeys)
            AllKnownDiscKeys.TryGetValue(discKey, out allMatchingKeys);
        var discKeyInfo = allMatchingKeys?.FirstOrDefault(k => k.FullPath.Contains(ProductCode, StringComparison.OrdinalIgnoreCase) && k.FullPath.EndsWith(".ird", StringComparison.OrdinalIgnoreCase))
                          ?? allMatchingKeys?.FirstOrDefault(k => k.FullPath.EndsWith(".ird", StringComparison.OrdinalIgnoreCase))
                          ?? allMatchingKeys?.First();
        DiscKeyFilename = Path.GetFileName(discKeyInfo?.FullPath);
        DiscKeyType = discKeyInfo?.KeyType ?? default;
    }

    public async Task DumpAsync(string output)
    {
        // check and create output folder
        var dumpPath = output;
        while (!string.IsNullOrEmpty(dumpPath) && !Directory.Exists(dumpPath))
        {
            var parent = Path.GetDirectoryName(dumpPath);
            if (parent == null || parent == dumpPath)
                dumpPath = null;
            else
                dumpPath = parent;
        }
        if (filesystemStructure is null)
            (filesystemStructure, emptyDirStructure) = GetFilesystemStructure();
        var validators = GetValidationInfo();
        if (!string.IsNullOrEmpty(dumpPath))
        {
            var fullOutputPath = Path.GetFullPath(output);
            var drive = DriveInfo.GetDrives()
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .ThenBy(d => d.RootDirectory.FullName, StringComparer.Ordinal)
                .FirstOrDefault(d => fullOutputPath.StartsWith(d.RootDirectory.FullName));
            if (drive != null)
            {
                var spaceAvailable = drive.AvailableFreeSpace;
                TotalFileSize = filesystemStructure.Sum(f => f.Length);
                var diff = TotalFileSize + 100 * 1024 - spaceAvailable;
                if (diff > 0)
                    Log.Warn($"Target drive might require {diff.AsStorageUnit()} of additional free space");
            }
        }

        foreach (var dir in emptyDirStructure)
            Log.Trace($"Empty dir: {dir}");
        foreach (var file in filesystemStructure)
            Log.Trace($"0x{file.StartSector:x8}: {file.TargetFileName} ({file.Length}, {file.FileInfo.CreationTimeUtc:u})");
        var outputPathBase = Path.Combine(output, OutputDir);
        Log.Debug($"Output path: {outputPathBase}");
        if (!Directory.Exists(outputPathBase))
            Directory.CreateDirectory(outputPathBase);

        TotalFileCount = filesystemStructure.Count;
        TotalSectors = discReader.TotalClusters;
        Log.Debug("Using decryption key: " + allMatchingKeys.First().DecryptedKeyId);
        var decryptionKey = allMatchingKeys.First().DecryptedKey;
        var sectorSize = (int)discReader.ClusterSize;
        var unprotectedRegions = driveStream.GetUnprotectedRegions();
        ValidationStatus = true;

        foreach (var dir in emptyDirStructure)
        {
            try
            {
                if (Cts.IsCancellationRequested)
                    return;

                var convertedName = dir.TargetDirName;
                if (Path.DirectorySeparatorChar != '\\')
                    convertedName = convertedName.Replace('\\', Path.DirectorySeparatorChar);
                var outputName = Path.Combine(outputPathBase, convertedName);
                if (!Directory.Exists(outputName))
                {
                    Log.Debug("Creating empty directory " + outputName);
                    Directory.CreateDirectory(outputName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                BrokenFiles.Add((dir.TargetDirName, "Unexpected error: " + ex.Message));
            }
        }

        foreach (var file in filesystemStructure)
        {
            try
            {
                if (Cts.IsCancellationRequested)
                    return;

                Log.Info($"Reading {file.TargetFileName} ({file.Length.AsStorageUnit()})");
                CurrentFileNumber++;
                var convertedFilename = Path.DirectorySeparatorChar == '\\' ? file.TargetFileName : file.TargetFileName.Replace('\\', Path.DirectorySeparatorChar);
                var inputFilename = Path.Combine(InputDevicePath, convertedFilename);

                if (!File.Exists(inputFilename))
                {
                    Log.Error($"Missing {file.TargetFileName}");
                    BrokenFiles.Add((file.TargetFileName, "missing"));
                    continue;
                }

                var outputFilename = Path.Combine(outputPathBase, convertedFilename);
                var fileDir = Path.GetDirectoryName(outputFilename);
                if (!Directory.Exists(fileDir))
                {
                    Log.Debug("Creating directory " + fileDir);
                    Directory.CreateDirectory(fileDir);
                }

                var error = false;
                var expectedHashes = (
                    from v in validators
                    where v.Files.ContainsKey(file.TargetFileName)
                    select v.Files[file.TargetFileName].Hashes
                ).ToList();
                var lastHash = "";
                var tries = 2;
                do
                {
                    try
                    {
                        tries--;
                        await using var outputStream = File.Open(outputFilename, FileMode.Create, FileAccess.Write, FileShare.Read);
                        await using var inputStream = File.Open(inputFilename, FileMode.Open, FileAccess.Read, FileShare.Read);
                        await using var decrypter = new Decrypter(inputStream, driveStream, decryptionKey, file.StartSector, sectorSize, unprotectedRegions);
                        Decrypter = decrypter;
                        await decrypter.CopyToAsync(outputStream, 8 * 1024 * 1024, Cts.Token).ConfigureAwait(false);
                        await outputStream.FlushAsync();
                        error = false;

                        var resultHashes = decrypter.GetHashes();
                        var resultMd5 = resultHashes["MD5"];
                        if (decrypter.WasEncrypted && decrypter.WasUnprotected)
                            Log.Debug("Partially decrypted " + file.TargetFileName);
                        else if (decrypter.WasEncrypted)
                            Log.Debug("Decrypted " + file.TargetFileName);

                        if (!expectedHashes.Any())
                        {
                            if (ValidationStatus == true)
                                ValidationStatus = null;
                        }
                        else if (!IsMatch(resultHashes, expectedHashes))
                        {
                            error = true;
                            var msg = "Unexpected hash: " + resultMd5;
                            if (resultMd5 == lastHash || decrypter.LastBlockCorrupted)
                            {
                                Log.Error(msg);
                                BrokenFiles.Add((file.TargetFileName, "corrupted"));
                                break;
                            }
                            Log.Warn(msg + ", retrying");
                        }

                        lastHash = resultMd5;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, e.Message);
                        error = true;
                        if (tries == 0)
                        {
                            BrokenFiles.Add((file.TargetFileName, "failed to read"));
                            ValidationStatus = false;
                        }
                    }
                } while (error && tries > 0 && !Cts.IsCancellationRequested);

                _ = new FileInfo(outputFilename)
                {
                    CreationTimeUtc = file.FileInfo.CreationTimeUtc,
                    LastWriteTimeUtc = file.FileInfo.LastWriteTimeUtc
                };
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                BrokenFiles.Add((file.TargetFileName, "Unexpected error: " + ex.Message));
                ValidationStatus = false;
            }
        }

        Log.Info("Fixing directory modification time stamps...");
        var fullDirectoryList = filesystemStructure
            .Select(f => f.Parent)
            .Concat(emptyDirStructure)
            .Distinct()
            .OrderByDescending(d => d.TargetDirName, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in fullDirectoryList)
        {
            try
            {
                var targetDirPath = Path.Combine(outputPathBase, dir.TargetDirName.TrimStart('\\'));
                _ = new DirectoryInfo(targetDirPath)
                {
                    CreationTimeUtc = dir.DirInfo.CreationTimeUtc,
                    LastWriteTimeUtc = dir.DirInfo.LastWriteTimeUtc
                };
            }
            catch (Exception e)
            {
                Log.Warn(e, $"Failed to fix timestamp for directory {dir.TargetDirName}");
            }
        }
        Log.Info("Completed");
    }

    public static async Task<(Version, GitHubReleaseInfo)> CheckUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient();
            var curVerMatch = VersionParts.Match(Version);
            var curVerStr = curVerMatch.Groups["ver"].Value;
            var curVerPre = curVerMatch.Groups["pre"].Value;
            client.DefaultRequestHeaders.UserAgent.Add(new("PS3DiscDumper", curVerStr));
            var responseJson = await client.GetStringAsync("https://api.github.com/repos/13xforever/ps3-disc-dumper/releases").ConfigureAwait(false);
            var releaseList = JsonSerializer.Deserialize<List<GitHubReleaseInfo>>(responseJson, JsonOptions);
            releaseList = releaseList?.OrderByDescending(r => System.Version.TryParse(r.TagName.TrimStart('v'), out var v) ? v : null).ToList();
            var latest = releaseList?.FirstOrDefault(r => !r.Prerelease);
            var latestBeta = releaseList?.FirstOrDefault(r => r.Prerelease);
            System.Version.TryParse(curVerStr, out var curVer);
            System.Version.TryParse(latest?.TagName.TrimStart('v') ?? "0", out var latestVer);
            var latestBetaMatch = VersionParts.Match(latestBeta?.TagName ?? "");
            var latestBetaVerStr = latestBetaMatch.Groups["ver"].Value;
            var latestBetaVerPre = latestBetaMatch.Groups["pre"].Value;
            System.Version.TryParse("0" + latestBetaVerStr, out var latestBetaVer);
            if (latestVer > curVer || latestVer == curVer && curVerPre is {Length: >0})
            {
                Log.Warn($"Newer version available: v{latestVer}\n\n{latest?.Name}\n{"".PadRight(latest?.Name.Length ?? 0, '-')}\n{latest?.Body}\n{latest?.HtmlUrl}\n");
                return (latestVer, latest);
            }
                
            if (latestBetaVer > curVer
                || (latestBetaVer == curVer
                    && curVerPre is {Length: >0}
                    && (latestBetaVerPre is {Length: >0} && StringComparer.OrdinalIgnoreCase.Compare(latestBetaVerPre, curVerPre) > 0
                        || latestBetaVerStr is null or "")))
            {
                Log.Warn($"Newer prerelease version available: v{latestBeta!.TagName.TrimStart('v')}\n\n{latestBeta?.Name}\n{"".PadRight(latestBeta?.Name.Length ?? 0, '-')}\n{latestBeta?.Body}\n{latestBeta?.HtmlUrl}\n");
                return (latestBetaVer, latestBeta);
            }
        }
        catch
        {
            Log.Warn("Failed to check for updates");
        }
        return (null, null);
    }

        
    private (List<FileRecord> files, List<DirRecord> dirs) GetFilesystemStructure()
    {
        var pos = driveStream.Position;
        var buf = new byte[64 * 1024 * 1024];
        driveStream.Seek(0, SeekOrigin.Begin);
        driveStream.ReadExact(buf, 0, buf.Length);
        driveStream.Seek(pos, SeekOrigin.Begin);
        try
        {
            using var memStream = new MemoryStream(buf, false);
            var reader = new CDReader(memStream, true, true);
            return reader.GetFilesystemStructure();
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to buffer TOC");
        }
        return discReader.GetFilesystemStructure();
    }

    private List<DiscInfo.DiscInfo> GetValidationInfo()
    {
        var discInfoList = new List<DiscInfo.DiscInfo>();
        foreach (var discKeyInfo in allMatchingKeys.Where(ki => ki.KeyType == KeyType.Ird))
        {
            var ird = IrdParser.Parse(File.ReadAllBytes(discKeyInfo.FullPath));
            if (!DiscVersion.Equals(ird.GameVersion))
                continue;

            discInfoList.Add(ird.ToDiscInfo());
        }
        return discInfoList;
    }

    private bool IsValidDiscKey(string discKeyId)
    {
        HashSet<DiscKeyInfo> keys;
        lock (AllKnownDiscKeys)
            if (!AllKnownDiscKeys.TryGetValue(discKeyId, out keys))
                return false;

        var key = keys.FirstOrDefault()?.DecryptedKey;
        if (key == null)
            return false;

        return IsValidDiscKey(key);
    }

    public bool IsValidDiscKey(byte[] discKey)
    {
        try
        {
            var licBytes = Decrypter.DecryptSector(discKey, detectionSector, sectorIV);
            return licBytes.Take(detectionBytesExpected.Length).SequenceEqual(detectionBytesExpected);
        }
        catch (Exception e)
        {
            Log.Error(e);
            Log.Error($"{nameof(discKey)} ({discKey.Length*8} bit): {discKey.ToHexString()}");
            Log.Error($"{nameof(sectorIV)} ({sectorIV.Length * 8} bit): {sectorIV.ToHexString()}");
            Log.Error($"{nameof(detectionSector)} ({detectionSector.Length} byte): {detectionSector.ToHexString()}");
            throw;
        }
    }

    private static bool IsMatch(Dictionary<string, string> hashes, List<Dictionary<string, string>> expectedHashes)
    {
        foreach (var eh in expectedHashes)
        foreach (var h in hashes)
            if (eh.TryGetValue(h.Key, out var expectedHash) && expectedHash == h.Value)
                return true;
        return false;
    }

    public void Dispose()
    {
        driveStream?.Dispose();
        ((IDisposable)Decrypter)?.Dispose();
        Cts?.Dispose();
    }
}