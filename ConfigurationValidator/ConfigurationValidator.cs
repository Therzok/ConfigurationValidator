using System;
using System.Linq;
using MonoDevelop.Components.Commands;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Projects;
using MonoDevelop.Core.ProgressMonitoring;

namespace ConfigurationValidator
{
	enum Commands
	{
		Validate,
	}

	public class ConfigurationValidatorHandler : CommandHandler
	{
		// TODO: Custom rules.
		// TODO: UI.

		static bool shouldSave;
		static void LogIssue (DotNetProject project, string propertyName, object expectedValue, object actualValue)
		{
			LoggingService.LogDebug ("{0}: '{1}' -> '{2}' on {3}",
				propertyName, actualValue, expectedValue, project.Name);
		}

		static void CheckConfigurationMappings (DotNetProject project, string currentConfig)
		{
			var projConfig = (DotNetProjectConfiguration)project.GetConfiguration (IdeApp.Workspace.ActiveConfiguration);
			if (project.GetConfigurations ().Contains (currentConfig) && projConfig.Name != currentConfig)
				LogIssue (project, "configuration", currentConfig, projConfig.Name);

			if (currentConfig.IndexOf ("Debug", StringComparison.OrdinalIgnoreCase) != -1)
				return;

			// Fixup entries for release configs.
			var debugEntry = project.ParentSolution
				.GetConfiguration (new SolutionConfigurationSelector (currentConfig.Replace ("Release", "Debug")))
				.GetEntryForItem (project);
			if (debugEntry == null)
				return;
			
			IdeApp.Workspace.ActiveConfigurationId = currentConfig;

			var entry = project.ParentSolution.GetConfiguration (IdeApp.Workspace.ActiveConfiguration).GetEntryForItem (project);
			entry.Build = debugEntry.Build;
			entry.Deploy = debugEntry.Deploy;

			var newConfig = debugEntry.ItemConfiguration.Replace ("Debug", "Release");
			if (project.GetConfigurations ().Any (config => config == newConfig))
				entry.ItemConfiguration = newConfig;
			else {
				LogIssue (project, "configuration", newConfig, "Missing");
				entry.ItemConfiguration = debugEntry.ItemConfiguration;
			}
		}

		static void CheckDefineSymbols (DotNetProject project)
		{
			var projConfig = (DotNetProjectConfiguration)project.GetConfiguration (IdeApp.Workspace.ActiveConfiguration);
			bool shouldContainDebug = projConfig.Name.IndexOf ("Debug", StringComparison.OrdinalIgnoreCase) != -1;
			bool shouldContainMac = projConfig.Name.IndexOf ("Mac", StringComparison.OrdinalIgnoreCase) != -1;
			bool shouldContainWin = projConfig.Name.IndexOf ("Win32", StringComparison.OrdinalIgnoreCase) != -1;

			if (projConfig.GetDefineSymbols ().Any (symbol => symbol.Equals ("DEBUG", StringComparison.OrdinalIgnoreCase)) != shouldContainDebug) {
				var expected = shouldContainDebug ? "DEBUG" : "";
				var actual = shouldContainDebug ? "" : "DEBUG";
				LogIssue (project, "symbols", expected, actual);
			}

			if (projConfig.GetDefineSymbols ().Any (symbol => symbol.Equals ("MAC", StringComparison.OrdinalIgnoreCase)) != shouldContainMac) {
				var expected = shouldContainMac ? "MAC" : "";
				var actual = shouldContainMac ? "" : "MAC";
				LogIssue (project, "symbols", expected, actual);
			}

			if (projConfig.GetDefineSymbols ().Any (symbol => symbol.Equals ("WIN32", StringComparison.OrdinalIgnoreCase)) != shouldContainWin) {
				var expected = shouldContainWin ? "WIN32" : "";
				var actual = shouldContainWin ? "" : "WIN32";
				LogIssue (project, "symbols", expected, actual);
			}
		}

		static void CheckConfigurationProperties (DotNetProject project)
		{
			var projConfig = (DotNetProjectConfiguration)project.GetConfiguration (IdeApp.Workspace.ActiveConfiguration);
			bool isDebug = projConfig.Name.IndexOf ("Debug", StringComparison.OrdinalIgnoreCase) != -1;
			bool shouldHaveDebugSymbols = true; // isDebug; // This is for general case.
			bool shouldBeOptimized = !isDebug;
			string[] debugTypeValues = isDebug ? new[] { "full" } : new[] { "pdbonly" }; // Should be none for release, but MD is pdbonly.

			if (projConfig.DebugMode != shouldHaveDebugSymbols) {
				LogIssue (project, "DebugSymbols", shouldHaveDebugSymbols, projConfig.DebugMode);
				projConfig.DebugMode = shouldHaveDebugSymbols;
				shouldSave = true;
			}

			var args = projConfig.CompilationParameters as MonoDevelop.CSharp.Project.CSharpCompilerParameters;
			if (args == null)
				return;
			
			if (!debugTypeValues.Any (value => value.Equals (args.DebugType, StringComparison.OrdinalIgnoreCase))) {
				LogIssue (project, "DebugType", debugTypeValues.First (), args.DebugType);
				args.DebugType = debugTypeValues.First ();
				shouldSave = true;
			}

			if (args.Optimize != shouldBeOptimized) {
				LogIssue (project, "Optimize", shouldBeOptimized, args.Optimize);
				args.Optimize = shouldBeOptimized;
				shouldSave = true;
			}

			if (isDebug)
				return;

			// Fixup to release properties.
			var debugConfig = (DotNetProjectConfiguration)project.GetConfiguration (new ItemConfigurationSelector (projConfig.Name.Replace ("Release", "Debug")));
			projConfig.DelaySign = debugConfig.DelaySign;
			projConfig.OutputAssembly = debugConfig.OutputAssembly;
			projConfig.SignAssembly = debugConfig.SignAssembly;
			projConfig.CommandLineParameters = debugConfig.CommandLineParameters;
			projConfig.ExternalConsole = debugConfig.ExternalConsole;
			projConfig.OutputDirectory = debugConfig.OutputDirectory;
			projConfig.RunWithWarnings = debugConfig.RunWithWarnings;
			projConfig.PauseConsoleOutput = debugConfig.PauseConsoleOutput;

			var debugArgs = debugConfig.CompilationParameters as MonoDevelop.CSharp.Project.CSharpCompilerParameters;
			if (debugArgs == null)
				return;

			args.DefineSymbols = debugArgs.DefineSymbols.Replace ("DEBUG", "").Trim(',', ';').Replace(",,", ",").Replace(";;", ";");
			args.DocumentationFile = debugArgs.DocumentationFile;
			args.GenerateOverflowChecks = debugArgs.GenerateOverflowChecks;
			args.LangVersion = debugArgs.LangVersion;
			args.NoStdLib = debugArgs.NoStdLib;
			args.NoWarnings = debugArgs.NoWarnings;
			args.TreatWarningsAsErrors = debugArgs.TreatWarningsAsErrors;
			args.PlatformTarget = debugArgs.PlatformTarget;
			args.UnsafeCode = debugArgs.UnsafeCode;
			args.WarningLevel = debugArgs.WarningLevel;
			args.WarningsNotAsErrors = debugArgs.WarningsNotAsErrors;
		}

		protected override void Run ()
		{
			string initialConfig = IdeApp.Workspace.ActiveConfigurationId;
			foreach (var config in IdeApp.Workspace.GetConfigurations ()) {
				IdeApp.Workspace.ActiveConfigurationId = config;

				LoggingService.LogDebug ("{0}--Initiating check on configuration {1}--{2}", Environment.NewLine, config, Environment.NewLine);

				foreach (var project in IdeApp.Workspace.GetAllSolutionItems<DotNetProject> ()) {
					shouldSave = false;
					CheckConfigurationMappings (project, config);
					CheckDefineSymbols (project);
					CheckConfigurationProperties (project);
					// TODO: Check project references when we can inspect conditional references.

					if (shouldSave)
						project.Save (new NullProgressMonitor ());
				}

				IdeApp.Workspace.GetAllSolutions ().First ().Save (new NullProgressMonitor ());
			}

			foreach (var project in IdeApp.Workspace.GetAllSolutionItems<DotNetProject> ()) {
				var configs = project.GetConfigurations ();
				if (configs.Count (config => config.IndexOf ("Mac") != -1) % 2 != 0)
					LogIssue (project, "missing config", "ReleaseMac", "Mac");
				if (configs.Count (config => config.IndexOf ("Win32") != -1) % 2 != 0)
					LogIssue (project, "missing config", "ReleaseWin32", "Win32");
			}

			IdeApp.Workspace.ActiveConfigurationId = initialConfig;
		}
	}
}

