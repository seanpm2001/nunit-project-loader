#tool nuget:?package=GitVersion.CommandLine&version=5.0.0
#tool nuget:?package=NUnit.ConsoleRunner&version=3.12.0
#tool nuget:?package=NUnit.ConsoleRunner&version=3.11.1
#tool nuget:?package=NUnit.ConsoleRunner&version=3.10.0

//////////////////////////////////////////////////////////////////////
// ARGUMENTS  
//////////////////////////////////////////////////////////////////////

// NOTE: These two constants are set here because constants.cake
// isn't loaded until after the arguments are parsed.
//
// Since GitVersion is only used when running under
// Windows, the default version should be updated to the
// next version after each release.
const string DEFAULT_VERSION = "3.7.0";
const string DEFAULT_CONFIGURATION = "Release"; 

var target = Argument("target", "Default");
var packageVersion = Argument("version", DEFAULT_VERSION);

// Load additional cake files here since some of them
// depend on the arguments provided.
#load cake/parameters.cake

//////////////////////////////////////////////////////////////////////
// SETUP AND TEARDOWN
//////////////////////////////////////////////////////////////////////

Setup<BuildParameters>((context) =>
{
	var parameters = BuildParameters.Create(context);

	if (BuildSystem.IsRunningOnAppVeyor)
		AppVeyor.UpdateBuildVersion(parameters.PackageVersion + "-" + AppVeyor.Environment.Build.Number);

	Information("Building {0} version {1} of NUnit Project Loader.", parameters.Configuration, parameters.PackageVersion);

	return parameters;
});

//////////////////////////////////////////////////////////////////////
// DUMP SETTINGS
//////////////////////////////////////////////////////////////////////

Task("DumpSettings")
	.Does<BuildParameters>((parameters) =>
	{
		parameters.DumpSettings();
	});

//////////////////////////////////////////////////////////////////////
// CLEAN
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does<BuildParameters>((parameters) =>
{
    CleanDirectory(parameters.OutputDirectory);
});


//////////////////////////////////////////////////////////////////////
// INITIALIZE FOR BUILD
//////////////////////////////////////////////////////////////////////

Task("NuGetRestore")
    .Does(() =>
{
    NuGetRestore(SOLUTION_FILE, new NuGetRestoreSettings()
	{
		Source = PACKAGE_SOURCES
	});
});

//////////////////////////////////////////////////////////////////////
// BUILD
//////////////////////////////////////////////////////////////////////

Task("Build")
    .IsDependentOn("NuGetRestore")
    .Does<BuildParameters>((parameters) =>
    {
		if(IsRunningOnWindows())
		{
			MSBuild(SOLUTION_FILE, new MSBuildSettings()
				.SetConfiguration(parameters.Configuration)
				.SetMSBuildPlatform(MSBuildPlatform.Automatic)
				.SetVerbosity(Verbosity.Minimal)
				.SetNodeReuse(false)
				.SetPlatformTarget(PlatformTarget.MSIL)
			);
		}
		else
		{
			XBuild(SOLUTION_FILE, new XBuildSettings()
				.WithTarget("Build")
				.WithProperty("Configuration", parameters.Configuration)
				.SetVerbosity(Verbosity.Minimal)
			);
		}
    });

//////////////////////////////////////////////////////////////////////
// TEST
//////////////////////////////////////////////////////////////////////

Task("Test")
	.IsDependentOn("Build")
	.Does<BuildParameters>((parameters) =>
	{
		NUnit3(TEST_TARGET_FRAMEWORKS.Select(framework => System.IO.Path.Combine(
			parameters.OutputDirectory, framework, UNIT_TEST_ASSEMBLY)));
	});

//////////////////////////////////////////////////////////////////////
// PACKAGE
//////////////////////////////////////////////////////////////////////

Task("BuildNuGetPackage")
	.Does<BuildParameters>((parameters) =>
	{
		CreateDirectory(parameters.PackageDirectory);

		BuildNuGetPackage(parameters);
	});

Task("InstallNuGetPackage")
	.IsDependentOn("RemoveChocolateyPackageIfPresent") // So both are not present
	.Does<BuildParameters>((parameters) =>
	{
		CleanDirectory(parameters.NuGetInstallDirectory);
		Unzip(parameters.NuGetPackage, parameters.NuGetInstallDirectory);

		Information($"Unzipped {parameters.NuGetPackageName} to { parameters.NuGetInstallDirectory}");
	});

Task("VerifyNuGetPackage")
	.IsDependentOn("InstallNuGetPackage")
	.Does<BuildParameters>((parameters) =>
	{
		Check.That(parameters.NuGetInstallDirectory,
			HasFiles("CHANGES.txt", "LICENSE.txt"),
			HasDirectory("tools").WithFile("nunit-project-loader.dll"));
		Information("Verification was successful!");
	});

Task("TestNuGetPackage")
	.IsDependentOn("InstallNuGetPackage")
	.Does<BuildParameters>((parameters) =>
	{
		new NuGetPackageTester(parameters).RunPackageTests();
	});

Task("RemoveNuGetPackageIfPresent")
	.Does<BuildParameters>((parameters) =>
	{
		if(DirectoryExists(parameters.NuGetInstallDirectory))
			DeleteDirectory(parameters.NuGetInstallDirectory,
				new DeleteDirectorySettings() { Recursive = true });
	});

Task("BuildChocolateyPackage")
	.IsDependentOn("Build")
	.Does<BuildParameters>((parameters) =>
	{
		CreateDirectory(parameters.PackageDirectory);

		BuildChocolateyPackage(parameters);
	});

Task("InstallChocolateyPackage")
	.IsDependentOn("RemoveNuGetPackageIfPresent") // So both are not present
	.Does<BuildParameters>((parameters) =>
	{
		CleanDirectory(parameters.ChocolateyInstallDirectory);
		Unzip(parameters.ChocolateyPackage, parameters.ChocolateyInstallDirectory);

		Information($"Unzipped {parameters.ChocolateyPackageName} to { parameters.ChocolateyInstallDirectory}");
	});

Task("VerifyChocolateyPackage")
	.IsDependentOn("InstallChocolateyPackage")
	.Does<BuildParameters>((parameters) =>
	{
		Check.That(parameters.ChocolateyInstallDirectory,
			HasDirectory("tools").WithFiles(
				"CHANGES.txt", "LICENSE.txt", "VERIFICATION.txt", "nunit-project-loader.dll"));
		Information("Verification was successful!");
	});

Task("TestChocolateyPackage")
	.IsDependentOn("InstallChocolateyPackage")
	.Does<BuildParameters>((parameters) =>
	{
		new ChocolateyPackageTester(parameters).RunPackageTests();
	});

Task("RemoveChocolateyPackageIfPresent")
	.Does<BuildParameters>((parameters) =>
	{
		if (DirectoryExists(parameters.ChocolateyInstallDirectory))
			DeleteDirectory(parameters.ChocolateyInstallDirectory,
				new DeleteDirectorySettings() { Recursive = true });
	});

//////////////////////////////////////////////////////////////////////
// PUBLISH
//////////////////////////////////////////////////////////////////////

static bool hadPublishingErrors = false;

Task("PublishPackages")
	.Description("Publish nuget and chocolatey packages according to the current settings")
	.IsDependentOn("PublishToMyGet")
	.IsDependentOn("PublishToNuGet")
	//.IsDependentOn("PublishToChocolatey")
	.Does(() =>
	{
		if (hadPublishingErrors)
			throw new Exception("One of the publishing steps failed.");
	});

// This task may either be run by the PublishPackages task,
// which depends on it, or directly when recovering from errors.
Task("PublishToMyGet")
	.Description("Publish packages to MyGet")
	.Does<BuildParameters>((parameters) =>
	{
		if (!parameters.ShouldPublishToMyGet)
			Information("Nothing to publish to MyGet from this run.");
		else
			try
			{
				PushNuGetPackage(parameters.NuGetPackage, parameters.MyGetApiKey, parameters.MyGetPushUrl);
				PushChocolateyPackage(parameters.ChocolateyPackage, parameters.MyGetApiKey, parameters.MyGetPushUrl);
			}
			catch (Exception)
			{
				hadPublishingErrors = true;
			}
	});

// This task may either be run by the PublishPackages task,
// which depends on it, or directly when recovering from errors.
Task("PublishToNuGet")
	.Description("Publish packages to NuGet")
	.Does<BuildParameters>((parameters) =>
	{
		if (!parameters.ShouldPublishToNuGet)
			Information("Nothing to publish to NuGet from this run.");
		else
			try
			{
				PushNuGetPackage(parameters.NuGetPackage, parameters.NuGetApiKey, parameters.NuGetPushUrl);
			}
			catch (Exception)
			{
				hadPublishingErrors = true;
			}
	});

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Package")
	.IsDependentOn("Build")
	.IsDependentOn("PackageNuGet")
	.IsDependentOn("PackageChocolatey");

Task("PackageNuGet")
	.IsDependentOn("BuildNuGetPackage")
	.IsDependentOn("VerifyNuGetPackage")
	.IsDependentOn("TestNuGetPackage");

Task("PackageChocolatey")
	.IsDependentOn("BuildChocolateyPackage")
	.IsDependentOn("VerifyChocolateyPackage")
	.IsDependentOn("TestChocolateyPackage");

Task("Full")
	.IsDependentOn("Clean")
	.IsDependentOn("Build")
	.IsDependentOn("Test")
	.IsDependentOn("Package");

Task("Travis")
	.IsDependentOn("Build")
	.IsDependentOn("Test");

Task("Appveyor")
	.IsDependentOn("Build")
	.IsDependentOn("Test")
	.IsDependentOn("Package")
	.IsDependentOn("PublishPackages");

Task("Default")
    .IsDependentOn("Build");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);
