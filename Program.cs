using System;
using System.CommandLine;
using System.IO;
#nullable enable 

namespace QWCModelTool
{
    class Program
    {
        static void Main(string[] args)
        {
            var fileOption = new Option<FileInfo>(
            name: "--file",
            description: "The FPM file to convert")
            {
                IsRequired = true
            };
            var outputFileOption = new Option<FileInfo?>(
            name: "--out",
            description: "The output glb file name");
            var exportLmOption = new Option<bool>(
            name: "--nolm",       
            description: "Skip lightmap export",
            getDefaultValue: () => false);
            var lmAlphaOption = new Option<float>(
            name: "--lmalp",
            description: "Fixed alpha value for lightmap textures. Ignored if --nolm is set",
            getDefaultValue: () => 0.5f);

            var rootCommand = new RootCommand("Tool for extracting FPM models used in HP QWC")
            {
                fileOption,
                outputFileOption,
                exportLmOption,
                lmAlphaOption
            };

            rootCommand.SetHandler((FileInfo file, FileInfo? outputFile, bool noExportLm, float lmAlpha) =>
            {
                float? alphaVal = !noExportLm ? lmAlpha : null;
                string outputPath = outputFile?.FullName ?? Path.ChangeExtension(file.FullName, "glb");
                using var fpm = new FPMFile(file.FullName);
                fpm.Decode();
                fpm.SaveMeshHierarchy(outputPath, alphaVal);
                Console.WriteLine($"Saved to {outputPath}");
            },
                fileOption,
                outputFileOption,
                exportLmOption,
                lmAlphaOption);

            rootCommand.Invoke(args);
        }
    }
}