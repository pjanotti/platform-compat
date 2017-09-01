using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Cci.Extensions;
using SharpCompress.Archives;
using SharpCompress.Archives.Tar;
using SharpCompress.Archives.Zip;
using SharpCompress.Compressors;
using SharpCompress.Compressors.Deflate;
using SharpCompress.Readers;
using Terrajobst.Csv;
using Terrajobst.PlatformCompat.Scanner;

namespace ex_gen
{
    internal static class Program
    {
        const string ExclusionFileSwitch = "-exc";
        const string SourcePathSwitch = "-src";

        private static int Main(string[] args)
        {
            if (!TryParseArguments(args, out string exclusionFile, out string sourcePath, out string outputPath))
            {
                var toolName = Path.GetFileNameWithoutExtension(Environment.GetCommandLineArgs()[0]);
                Console.Error.WriteLine($"Usage: {toolName} [options] <out-path>");
                Console.Error.WriteLine($"\nOptions:\n");
                Console.Error.WriteLine($"\t{ExclusionFileSwitch}:<exclusion-file>");
                Console.Error.WriteLine($"\t{SourcePathSwitch}:<sourch-path>");
                Console.Error.WriteLine();
                return 1;
            }

            try
            {
                Run(exclusionFile, sourcePath, outputPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static bool TryParseArguments(string[] args, out string exclusionFile, out string sourcePath, out string outputPath)
        {
            const int minArgsLength = 1;
            const int maxArgsLength = 3;

            exclusionFile = sourcePath = outputPath = null;
            if (args.Length < minArgsLength || args.Length > maxArgsLength)
                return false;

            for (var i = 0; i < args.Length - 1; ++i)
            {
                var tokens = args[i].Split(new[] { ':' }, 2);
                if (tokens.Length != 2)
                    return false;

                switch (tokens[0])
                {
                    case ExclusionFileSwitch:
                        exclusionFile = Path.GetFullPath(tokens[1]);
                        break;
                    case SourcePathSwitch:
                        sourcePath = Path.GetFullPath(tokens[1]);
                        break;
                    default:
                        return false;
                }
            }

            outputPath = Path.GetFullPath(args[args.Length - 1]);

            return true;
        }

        private static void Run(string exclusionFile, string sourcePath, string outputPath)
        {
            string tempFolder = null;
            try
            {
                if (sourcePath == null)
                {
                    tempFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                    Directory.CreateDirectory(tempFolder);

                    var rootUrl = "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/master/";
                    var files = new[]
                    {
                        "dotnet-dev-win-x64.latest.zip",
                        "dotnet-dev-osx-x64.latest.tar.gz",
                        "dotnet-dev-linux-x64.latest.tar.gz"
                    };

                    DownloadFiles(rootUrl, files, tempFolder);
                    ExtractFiles(tempFolder);
                    sourcePath = tempFolder;
                }

                var database = Scan(sourcePath, exclusionFile);
                ExportCsv(database, outputPath);
            }
            finally
            {
                if (tempFolder != null)
                    Directory.Delete(tempFolder, true);
            }
        }

        private static void DownloadFiles(string rootUrl, string[] files, string tempFolder)
        {
            Console.WriteLine("Downloading files...");

            foreach (var file in files)
            {
                Console.WriteLine("\t" + file);

                var downloadUrl = rootUrl + file;
                var localPath = Path.Combine(tempFolder, file);

                WebClient client = new WebClient();
                client.DownloadFile(downloadUrl, localPath);
            }
        }

        private static void ExtractFiles(string tempFolder)
        {
            Console.WriteLine("Extracting files...");

            ExpandGzipStreams(tempFolder);
            ExtractArchives(tempFolder);
        }

        private static void ExpandGzipStreams(string directory)
        {
            var gzFiles = Directory.EnumerateFiles(directory, "*.gz");

            foreach (var gzFile in gzFiles)
            {
                Console.WriteLine("\t" + Path.GetFileName(gzFile));

                var source = gzFile;
                var target = Path.Combine(directory, Path.GetFileNameWithoutExtension(source));

                using (var sourceStream = File.OpenRead(source))
                using (var gzStream = new GZipStream(sourceStream, CompressionMode.Decompress))
                using (var targetSteam = File.Create(target))
                    gzStream.CopyTo(targetSteam);

                File.Delete(source);
            }
        }

        private static void ExtractArchives(string tempFolder)
        {
            var options = new ExtractionOptions { ExtractFullPath = true, Overwrite = true };

            foreach (var fileName in Directory.GetFiles(tempFolder))
            {
                Console.WriteLine("\t" + Path.GetFileName(fileName));

                var extractedFolderName = Path.GetFileNameWithoutExtension(fileName).Replace(".tar", "");
                var exractedFolderPath = Path.Combine(tempFolder, extractedFolderName);

                using (var fileStream = File.OpenRead(fileName))
                using (var archive = OpenArchive(fileStream))
                {
                    var selectedEntries = archive.Entries.Where(
                        e => e.Key.StartsWith(@"./shared/Microsoft.NETCore.App/") ||
                        e.Key.StartsWith(@"shared/Microsoft.NETCore.App/"));
                    if (!selectedEntries.Any())
                        throw new ArgumentException($"No archive selected to be extracted from {fileName} at {tempFolder}");

                    foreach (var entry in selectedEntries)
                        entry.WriteToDirectory(exractedFolderPath, options);
                }

                File.Delete(fileName);
            }
        }

        private static IArchive OpenArchive(FileStream fileStream)
        {
            var extension = Path.GetExtension(fileStream.Name);
            switch (extension)
            {
                case ".tar":
                    return TarArchive.Open(fileStream);
                case ".zip":
                    return ZipArchive.Open(fileStream);
                default:
                    throw new NotImplementedException($"Unknown file {extension}");
            }
        }

        private static Database Scan(string tempFolder, string exclusionFile)
        {
            Console.WriteLine("Analyzing...");
            var exclusionDatabase = exclusionFile != null ? ImportCsv(exclusionFile) : null;
            var result = new Database(exclusionDatabase);

            var platforms = EnumeratePlatformDirectories(tempFolder);

            foreach (var entry in platforms)
            {
                Console.WriteLine("\t" + entry.platform);

                var platform = entry.platform;
                var directory = entry.directory;
                var reporter = new DatabaseReporter(result, platform);

                var analyzer = new ExceptionScanner(reporter);
                var assemblies = HostEnvironment.LoadAssemblySet(directory);

                foreach (var assembly in assemblies)
                    analyzer.ScanAssembly(assembly);
            }

            return result;
        }

        private static IEnumerable<(string platform, string directory)> EnumeratePlatformDirectories(string tempFolder)
        {
            var roots = Directory.EnumerateDirectories(tempFolder);

            foreach (var root in roots)
            {
                var match = Regex.Match(root, @"dotnet-dev-([^-]+)-[^-]+.latest");
                var platform = match.Success ? match.Groups[1].Value : root;

                var sharedFrameworkFolder = Path.Combine(root, "shared", "Microsoft.NETCore.App");
                var version200Folder = Directory.EnumerateDirectories(sharedFrameworkFolder, "2.0.0*", SearchOption.TopDirectoryOnly).Single();

                yield return (platform, version200Folder);
            }
        }

        private static void ExportCsv(Database database, string path)
        {
            using (var streamWriter = new StreamWriter(path))
            {
                var writer = new CsvWriter(streamWriter);

                writer.Write("DocId");
                writer.Write("Namespace");
                writer.Write("Type");
                writer.Write("Member");

                foreach (var platform in database.Platforms)
                    writer.Write(platform);

                writer.WriteLine();

                foreach (var entry in database.Entries.OrderBy(e => e.NamespaceName)
                                                      .ThenBy(e => e.TypeName)
                                                      .ThenBy(e => e.MemberName)
                                                      .ThenBy(e => e.DocId))
                {
                    writer.Write(entry.DocId);
                    writer.Write(entry.NamespaceName);
                    writer.Write(entry.TypeName);
                    writer.Write(entry.MemberName);

                    foreach (var platform in database.Platforms)
                    {
                        var value = entry.Platforms.Contains(platform) ? "X" : "";
                        writer.Write(value);
                    }

                    writer.WriteLine();
                }
            }
        }

        private static Database ImportCsv(string path)
        {
            var database = new Database();

            using (var streamReader = new StreamReader(path))
            {
                var csvReader = new CsvReader(streamReader);
                var headerFields = csvReader.ReadLine();

                while (!streamReader.EndOfStream)
                {
                    var row = csvReader.ReadLine();
                    // Add the entry for each platform
                    for (var i = 4; i < headerFields.Length; ++i)
                    {
                        if (!string.IsNullOrWhiteSpace(row[i]))
                            database.Add(row[0], row[1], row[2], row[3], headerFields[i]);
                    }
                }
            }

            return database;
        }
    }
}
