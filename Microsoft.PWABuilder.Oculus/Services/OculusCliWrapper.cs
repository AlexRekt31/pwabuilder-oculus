﻿using Microsoft.Extensions.Options;
using Microsoft.PWABuilder.Oculus.Models;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;

namespace Microsoft.PWABuilder.Oculus.Services
{
    /// <summary>
    /// Wraps the Oculus CLI tool to allow for ease of execution.
    /// </summary>
    public class OculusCliWrapper
    {
        private readonly ProcessRunner procRunner;
        private readonly AppSettings appSettings;
        private readonly ILogger<OculusCliWrapper> logger;

        public OculusCliWrapper(
            ProcessRunner procRunner,
            IOptions<AppSettings> appSettings,
            ILogger<OculusCliWrapper> logger)
        {
            this.procRunner = procRunner;
            this.appSettings = appSettings.Value;
            this.logger = logger;
        }

        //Pass the manifest file

        public async Task<OculusCliResult> CreateApk(OculusAppPackageOptions.Validated packageOptions, string outputDirectory, string manifestFilePath)
        {
            // Run the Oculus CLI tool.
            ProcessResult procResult;
            var apkPath = Path.Combine(outputDirectory, "output.apk");
            try
            {
                var signingKeyFilePath = await WriteSigningKeyToDisk(packageOptions, outputDirectory);
                var processArgs = CreateCommandLineArgs(packageOptions, apkPath, signingKeyFilePath, manifestFilePath);
                procResult = await procRunner.Run(appSettings.OculusCliPath, processArgs, TimeSpan.FromMinutes(5));
            }
            catch (ProcessException procError)
            {
                logger.LogError(procError, "Oculus CLI encountered an error. Standard error: {stdErr}{newLine}Standard out:{stdOut}", procError.StandardError, Environment.NewLine + Environment.NewLine, procError.StandardOutput);
                throw;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Oculus CLI encountered an error.");
                throw;
            }

            // Log success. Warn if we have any standard error output.
            logger.LogInformation("Oculus CLI process completed successfully. Output: {stdOutput}", procResult.StandardOutput);
            if (!string.IsNullOrEmpty(procResult.StandardError))
            {
                logger.LogWarning("Oculus CLI process completed successfully but output error information. {stdError}", procResult.StandardError);
            }

            // Ensure we have the APK. We've seen scenarios where the Oculus CLI says it succeeded, but in fact no APK was generated.
            if (!File.Exists(apkPath))
            {
                var error = new Exception("Oculus CLI claimed it finished successfully, but it didn't produce an APK.");
                error.Data.Add("Standard Error", procResult.StandardError);
                error.Data.Add("Standard Out", procResult.StandardOutput);
                logger.LogError(error, "Oculus CLI claimed it finished successfully, but it didn't produce an APK. Standard error: {stdError}{newLine}Standard output: {stdOutput}", procResult.StandardError, Environment.NewLine + Environment.NewLine, procResult.StandardOutput);
                throw error;                
            }

            return new OculusCliResult
            {
                ApkFilePath = apkPath
            };
        }

        private async Task<string?> WriteSigningKeyToDisk(OculusAppPackageOptions.Validated packageOptions, string outputDirectory)
        {
            // No signing key options? Then we have nothing to write to disk.
            if (packageOptions.SigningKey == null)
            {
                return null;
            }

            // Don't have a key store file? Nothing to write to disk.
            if (string.IsNullOrWhiteSpace(packageOptions.SigningKey.KeyStoreFile))
            {
                return null;
            }

            // If we're configured to skip signing, we have nothing to write to disk.
            if (packageOptions.SigningKey.SkipSigning)
            {
                return null;
            }

            // OK, we have a key file. Let's write it to disk.
            var keyStorePath = Path.Combine(outputDirectory, $"{Guid.NewGuid()}.keystore");
            try
            {
                var keyStoreBytes = Convert.FromBase64String(packageOptions.SigningKey.KeyStoreFile);
                await File.WriteAllBytesAsync(keyStorePath, keyStoreBytes);
                return keyStorePath;
            }
            catch (Exception error)
            {
                logger.LogError(error, "Error creating key store file for PWA {name} at {url}", packageOptions.Name, packageOptions.ManifestUri);
                throw;
            }
        }

        /// <summary>
        /// Creates command line arguments for the Oculus command line tool (ovr-platform-util.exe) from the specified options.
        /// </summary>
        /// <param name="manifestFilePath">The path to the manifest file on disk.</param>
        /// <param name="apkOutputFilePath">The desired file path of the generated APK.</param>
        /// <param name="signingKeyFilePath">The file path to the signing .keystore file. This will be null if no signing key is required.</param>
        /// <param name="options">The Oculus package creation options.</param>
        /// <returns>The command line arguments for the Oculus CLI.</returns>
        protected virtual string CreateCommandLineArgs(
            OculusAppPackageOptions.Validated options, 
            string apkOutputFilePath, 
            string? signingKeyFilePath,
            string manifestFilePath)
        {
            var args = new Dictionary<string, string?>
            {
                { "create-pwa", string.Empty },
                { "out", apkOutputFilePath },
                { "android-sdk", appSettings.AndroidSdkPath },
                { "manifest-content-file", manifestFilePath },
                { "web-manifest-url", options.ManifestUri.ToString() },
                { "package-name", options.PackageId },
                //{ "app-id", options.Name } // If we pass this arg, then Oculus CLI fails with "Error: APKTool at [name] is not executable", where name is options.Name.
            };

            // Generate an unsigned APK if we're instructed to do so.
            if (options.SigningKey?.SkipSigning == true)
            {
                args.Add("skip-sign", string.Empty);
            }

            // Append signing key information if we've been supplied with one.
            if (options.SigningKey != null && !string.IsNullOrEmpty(signingKeyFilePath))
            {
                args.Add("keystore", signingKeyFilePath);
                args.Add("ks-pass", options.SigningKey.StorePassword);
                args.Add("ks-key-alias", options.SigningKey.Alias);
                args.Add("key-pass", options.SigningKey.Password);
            }

            var builder = new StringBuilder();
            foreach (var arg in args)
            {
                if (!string.IsNullOrWhiteSpace(arg.Value))
                {
                    builder.Append($"--{arg.Key}=\"{arg.Value}\"");
                }
                else
                {
                    builder.Append(arg.Key);
                }
                builder.Append(' ');
            }

            return builder.ToString();
        }
    }
}
