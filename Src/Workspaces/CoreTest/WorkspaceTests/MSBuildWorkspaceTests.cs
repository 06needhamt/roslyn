﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.UnitTests.SolutionGeneration;
using Roslyn.Test.Utilities;
using Roslyn.Utilities;
using Xunit;
using CS = Microsoft.CodeAnalysis.CSharp;
using VB = Microsoft.CodeAnalysis.VisualBasic;

namespace Microsoft.CodeAnalysis.UnitTests
{
    public class MSBuildWorkspaceTests : MSBuildWorkspaceTestBase
    {
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCreateMSBuildWorkspace()
        {
            var workspace = MSBuildWorkspace.Create();

            Assert.NotNull(workspace);
            Assert.NotNull(workspace.Services);
            Assert.NotNull(workspace.Services.Workspace);
            Assert.Equal(workspace, workspace.Services.Workspace);
            Assert.NotNull(workspace.Services.HostServices);
            Assert.NotNull(workspace.Services.PersistentStorage);
            Assert.NotNull(workspace.Services.TemporaryStorage);
            Assert.NotNull(workspace.Services.TextFactory);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_SingleProjectSolution()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.Projects.First();
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
            Assert.NotNull(type);
            Assert.Equal(true, type.ToString().StartsWith("public class CSharpClass"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_MultiProjectSolution()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var vbProject = sol.Projects.First(p => p.Language == LanguageNames.VisualBasic);

            // verify the dependent project has the correct metadata references (and does not include the output for the project references)
            var references = vbProject.MetadataReferences.ToList();
            Assert.Equal(4, references.Count);
            var fileNames = new HashSet<string>(references.Select(r => Path.GetFileName(((PortableExecutableReference)r).FilePath)));
            Assert.Equal(true, fileNames.Contains("System.Core.dll"));
            Assert.Equal(true, fileNames.Contains("System.dll"));
            Assert.Equal(true, fileNames.Contains("Microsoft.VisualBasic.dll"));
            Assert.Equal(true, fileNames.Contains("mscorlib.dll"));

            // the compilation references should have the metadata reference to the csharp project skeleton assembly
            var compReferences = vbProject.GetCompilationAsync().Result.References.ToList();
            Assert.Equal(5, compReferences.Count);
        }

        [WorkItem(552981, "DevDiv")]
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_DuplicateProjectGuids()
        {
            CreateFiles(GetSolutionWithDuplicatedGuidFiles());

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("DuplicatedGuids.sln")).Result;
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOutputFilePaths()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);
            var p2 = sol.Projects.First(p => p.Language == LanguageNames.VisualBasic);

            Assert.Equal("CSharpProject.dll", Path.GetFileName(p1.OutputFilePath));
            Assert.Equal("VisualBasicProject.dll", Path.GetFileName(p2.OutputFilePath));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCrossLanguageReferencesUsesInMemoryGeneratedMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);
            var p2 = sol.Projects.First(p => p.Language == LanguageNames.VisualBasic);

            // prove there is no existing metadata on disk for this project
            Assert.Equal("CSharpProject.dll", Path.GetFileName(p1.OutputFilePath));
            Assert.Equal(false, File.Exists(p1.OutputFilePath));

            // prove that vb project refers to csharp project via generated metadata (skeleton) assembly. 
            // it should be a MetadataImageReference
            var c2 = p2.GetCompilationAsync().Result;
            var pref = c2.References.OfType<PortableExecutableReference>().FirstOrDefault(r => r.Display == "CSharpProject");
            Assert.NotNull(pref);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCrossLanguageReferencesWithOutOfDateMetadataOnDiskUsesInMemoryGeneratedMetadata()
        {
            PrepareCrossLanguageProjectWithEmittedMetadata();

            // recreate the solution so it will reload from disk
            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var p1 = sol.Projects.First(p => p.Language == LanguageNames.CSharp);

            // update project with top level change that should now invalidate use of metadata from disk
            var d1 = p1.Documents.First();
            var root = d1.GetSyntaxRootAsync().Result;
            var decl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().First();
            var newDecl = decl.WithIdentifier(CS.SyntaxFactory.Identifier("Pogrom").WithLeadingTrivia(decl.Identifier.LeadingTrivia).WithTrailingTrivia(decl.Identifier.TrailingTrivia));
            var newRoot = root.ReplaceNode(decl, newDecl);
            var newDoc = d1.WithSyntaxRoot(newRoot);
            p1 = newDoc.Project;
            var p2 = p1.Solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);

            // we should now find a MetadataImageReference that was generated instead of a MetadataFileReference
            var c2 = p2.GetCompilationAsync().Result;
            var pref = c2.References.OfType<PortableExecutableReference>().FirstOrDefault(r => r.Display == "EmittedCSharpProject");
            Assert.NotNull(pref);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestInternalsVisibleToSigned()
        {
            var solution = Solution(
                Project(
                    ProjectName("Project1"),
                    Sign,
                    Document(string.Format(
@"using System.Runtime.CompilerServices;
[assembly:InternalsVisibleTo(""Project2, PublicKey={0}"")]
class C1
{{
}}", PublicKey))),
                Project(
                    ProjectName("Project2"),
                    Sign,
                    ProjectReference("Project1"),
                    Document(@"class C2 : C1 { }")));

            var project2 = solution.GetProjectsByName("Project2").First();
            var compilation = project2.GetCompilationAsync().Result;
            var diagnostics = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error || d.Severity == DiagnosticSeverity.Warning)
                .ToArray();

            Assert.Equal(Enumerable.Empty<Diagnostic>(), diagnostics);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestVersions()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var sversion = solution.Version;
            var latestPV = solution.GetLatestProjectVersion();
            var project = solution.Projects.First();
            var pversion = project.Version;
            var document = project.Documents.First();
            var dversion = document.GetTextVersionAsync().Result;
            var latestDV = project.GetLatestDocumentVersionAsync().Result;

            // update document
            var solution1 = solution.WithDocumentText(document.Id, SourceText.From("using test;"));
            var document1 = solution1.GetDocument(document.Id);
            var dversion1 = document1.GetTextVersionAsync().Result;
            Assert.NotEqual(dversion, dversion1); // new document version
            Assert.Equal(true, dversion1.TestOnly_IsNewerThan(dversion));
            Assert.Equal(solution.Version, solution1.Version); // updating document should not have changed solution version
            Assert.Equal(project.Version, document1.Project.Version); // updating doc should not have changed project version
            var latestDV1 = document1.Project.GetLatestDocumentVersionAsync().Result;
            Assert.NotEqual(latestDV, latestDV1);
            Assert.Equal(true, latestDV1.TestOnly_IsNewerThan(latestDV));
            Assert.Equal(latestDV1, document1.GetTextVersionAsync().Result); // projects latest doc version should be this doc's version

            // update project
            var solution2 = solution1.WithProjectCompilationOptions(project.Id, project.CompilationOptions.WithOutputKind(OutputKind.NetModule));
            var document2 = solution2.GetDocument(document.Id);
            var dversion2 = document2.GetTextVersionAsync().Result;
            Assert.Equal(dversion1, dversion2); // document didn't change, so version should be the same.
            Assert.NotEqual(document1.Project.Version, document2.Project.Version); // project did change, so project versions should be different
            Assert.Equal(true, document2.Project.Version.TestOnly_IsNewerThan(document1.Project.Version));
            Assert.Equal(solution1.Version, solution2.Version); // solution didn't change, just individual project.

            // update solution
            var pid2 = ProjectId.CreateNewId();
            var solution3 = solution2.AddProject(pid2, "foo", "foo", LanguageNames.CSharp);
            Assert.NotEqual(solution2.Version, solution3.Version); // solution changed, added project.
            Assert.Equal(true, solution3.Version.TestOnly_IsNewerThan(solution2.Version));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_LoadMetadataForReferencedProjects()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            ws.LoadMetadataForReferencedProjects = true;
            var project = ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var expectedFileName = GetSolutionFileName(@"CSharpProject\CSharpClass.cs");
            Assert.Equal(expectedFileName, tree.FilePath);
            var type = tree.GetRoot().DescendantTokens().First(t => t.ToString() == "class").Parent;
            Assert.NotNull(type);
            Assert.Equal(true, type.ToString().StartsWith("public class CSharpClass"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_CSharp_WithoutPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutPrefer32Bit.csproj")));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_CSharp_WithoutPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithoutPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_CSharp_WithPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj")));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_CSharp_WithPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "Library"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_CSharp_WithPrefer32BitAndWinMDObj()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithPrefer32Bit.csproj"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputType", "winmdobj"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_CSharp_WithoutOutputPath()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "OutputPath", ""));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            Assert.NotEmpty(project.OutputFilePath);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_CSharp_WithoutAssemblyName()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "AssemblyName", ""));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            Assert.NotEmpty(project.OutputFilePath);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_VisualBasic_WithoutPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutPrefer32Bit.vbproj")));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_VisualBasic_WithoutPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithoutPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "Library"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_VisualBasic_WithPrefer32BitAndConsoleApplication()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj")));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu32BitPreferred, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_VisualBasic_WithPrefer32BitAndLibrary()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "Library"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(739043, "DevDiv")]
        public void TestOpenProject_VisualBasic_WithPrefer32BitAndWinMDObj()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputType", "winmdobj"));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            var compilation = project.GetCompilationAsync().Result;
            Assert.Equal(Platform.AnyCpu, compilation.Options.Platform);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_VisualBasic_WithoutOutputPath()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "OutputPath", ""));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            Assert.NotEmpty(project.OutputFilePath);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_VisualBasic_WithoutAssemblyName()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_WithPrefer32Bit.vbproj"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "AssemblyName", ""));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            Assert.NotEmpty(project.OutputFilePath);
        }

        [Fact(Skip = "707107"), Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithXaml()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_WithXaml.csproj"))
                .WithFile(@"CSharpProject\App.xaml", GetResourceText("CSharpProject_App.xaml"))
                .WithFile(@"CSharpProject\App.xaml.cs", GetResourceText("CSharpProject_App.xaml.cs"))
                .WithFile(@"CSharpProject\MainWindow.xaml", GetResourceText("CSharpProject_MainWindow.xaml"))
                .WithFile(@"CSharpProject\MainWindow.xaml.cs", GetResourceText("CSharpProject_MainWindow.xaml.cs")));

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var documents = project.Documents.ToList();

            // AssemblyInfo.cs, App.xaml.cs, MainWindow.xaml.cs, App.g.cs, MainWindow.g.cs, + unusual AssemblyAttributes.cs
            Assert.Equal(6, documents.Count);

            // both xaml code behind files are documents
            Assert.Equal(true, documents.Contains(d => d.Name == "App.xaml.cs"));
            Assert.Equal(true, documents.Contains(d => d.Name == "MainWindow.xaml.cs"));

            // prove no xaml files are documents
            Assert.Equal(false, documents.Contains(d => d.Name.EndsWith(".xaml")));

            // prove that generated source files for xaml files are included in documents list
            Assert.Equal(true, documents.Contains(d => d.Name == "App.g.cs"));
            Assert.Equal(true, documents.Contains(d => d.Name == "MainWindow.g.cs"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestMetadataReferenceHasBadHintPath()
        {
            // prove that even with bad hint path for metadata reference the workspace can succeed at finding the correct metadata reference.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_BadHintPath.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            var project = solution.Projects.First();
            var refs = project.MetadataReferences.ToList();
            var csharpLib = refs.OfType<PortableExecutableReference>().FirstOrDefault(r => r.FilePath.Contains("Microsoft.CSharp"));
            Assert.NotNull(csharpLib);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(531631, "DevDiv")]
        public void TestOpenProject_AssemblyNameIsPath()
        {
            // prove that even if assembly name is specified as a path instead of just a name, workspace still succeeds at opening project.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_AssemblyNameIsPath.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            var project = solution.Projects.First();
            var comp = project.GetCompilationAsync().Result;
            Assert.Equal("ReproApp", comp.AssemblyName);
            string expectedOutputPath = GetParentDirOfParentDirOfContainingDir(project.FilePath);
            Assert.Equal(expectedOutputPath, Path.GetDirectoryName(project.OutputFilePath));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(531631, "DevDiv")]
        public void TestOpenProject_AssemblyNameIsPath2()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_AssemblyNameIsPath2.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            var project = solution.Projects.First();
            var comp = project.GetCompilationAsync().Result;
            Assert.Equal("ReproApp", comp.AssemblyName);
            string expectedOutputPath = Path.Combine(Path.GetDirectoryName(project.FilePath), @"bin");
            Assert.Equal(expectedOutputPath, Path.GetDirectoryName(Path.GetFullPath(project.OutputFilePath)));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithDuplicateFile()
        {
            // Verify that we don't throw in this case
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject_DuplicateFile.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            var project = solution.Projects.First();
            var documents = project.Documents.ToList();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithInvalidFileExtension()
        {
            // make sure the file does in fact exist, but with an unrecognized exstension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj.nyi", GetResourceText("CSharpProject_CSharpProject.csproj")));

            AssertThrows<InvalidOperationException>(delegate
            {
                MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj.nyi")).Wait();
            },
            (e) =>
            {
                Assert.Equal(true, e.Message.Contains("extension"));
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_ProjectFileExtensionAssociatedWithUnknownLanguage()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            AssertThrows<InvalidOperationException>(delegate
            {
                var ws = MSBuildWorkspace.Create();
                ws.AssociateFileExtensionWithLanguage("csproj", "lingo"); // non-existent language
                ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Wait();
            },
            (e) =>
            {
                // the exception should tell us something about the language being unrecognized.
                Assert.Equal(true, e.Message.Contains("language"));
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithAssociatedLanguageExtension()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());
            var ws = MSBuildWorkspace.Create();
            
            // convince workspace that csharp projects are really visual basic (should cause lots of syntax errors)
            ws.AssociateFileExtensionWithLanguage("csproj", LanguageNames.VisualBasic);

            var project = ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            Assert.NotEqual(0, tree.GetDiagnostics().Count());
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithAssociatedLanguageExtension2()
        {
            // make a CSharp solution with a project file having the incorrect extension 'vbproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.vbproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            var ws = MSBuildWorkspace.Create();
            ws.AssociateFileExtensionWithLanguage("vbproj", LanguageNames.CSharp);
            var project = ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.vbproj")).Result;
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var diagnostics = tree.GetDiagnostics().ToList();
            Assert.Equal(0, diagnostics.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithAssociatedLanguageExtension3_IgnoreCase()
        {
            // make a CSharp solution with a project file having the incorrect extension 'anyproj', and then load it using the overload the lets us
            // specify the language directly, instead of inferring from the extension
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.anyproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            var ws = MSBuildWorkspace.Create();

            // prove that the association works even if the case is different
            ws.AssociateFileExtensionWithLanguage("ANYPROJ", LanguageNames.CSharp);
            var project = ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.anyproj")).Result;
            var document = project.Documents.First();
            var tree = document.GetSyntaxTreeAsync().Result;
            var diagnostics = tree.GetDiagnostics().ToList();
            Assert.Equal(0, diagnostics.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            AssertThrows<FileNotFoundException>(() =>
            {
                var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("NonExistentSolution.sln")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithInvalidSolutionFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            AssertThrows<InvalidOperationException>(() =>
            {
                var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"http://localhost/Invalid/InvalidSolution.sln")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithInvalidProjectPath_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_InvalidProjectPath.sln")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;

            Assert.Equal(1, diagnostics.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithInvalidProjectPath_SkipFalse_Fails()
        {
            // when not skipped we should get an exception for the invalid project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_InvalidProjectPath.sln")));

            var ws = MSBuildWorkspace.Create();
            ws.SkipUnrecognizedProjects = false;

            AssertThrows<InvalidOperationException>(() =>
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithNonExistentProject_SkipTrue_SucceedsWithFailureEvent()
        {
            // when skipped we should see a diagnostic for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_NonExistentProject.sln")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;

            Assert.Equal(1, diagnostics.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithNonExistentProject_SkipFalse_Fails()
        {
            // when skipped we should see an exception for the non-existent project

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_NonExistentProject.sln")));

            var ws = MSBuildWorkspace.Create();
            ws.SkipUnrecognizedProjects = false;

            AssertThrows<FileNotFoundException>(() =>
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            });
        }

#if !MSBUILD12
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithUnrecognizedProjectFileExtension_Fails()
        {
            // proves that for solution open, project type guid and extension are both necessary
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            Assert.Equal(0, solution.ProjectIds.Count);
        }
#else
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithUnrecognizedProjectFileExtension_Succeeds()
        {
            // proves that for solution open, project type guid is all that is necessary
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            Assert.Equal(1, solution.ProjectIds.Count);
        }
#endif

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithUnrecognizedProjectTypeGuidButRecognizedExtension_Succeeds()
        {
            // proves that if project type guid is not recognized, a known project file extension is all we need.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuid.sln")));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            Assert.Equal(1, solution.ProjectIds.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipTrue_SucceedsWithFailureEvent()
        {
            // proves that if both project type guid and file extension are unrecognized, then project is skipped.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuidAndUnknownExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;

            Assert.Equal(1, diagnostics.Count);
            Assert.Equal(0, solution.ProjectIds.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithUnrecognizedProjectTypeGuidAndUnrecognizedExtension_WithSkipFalse_Fails()
        {
            // proves that if both project type guid and file extension are unrecognized, then open project fails.
            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"TestSolution.sln", GetResourceText("TestSolution_CSharp_UnknownProjectTypeGuidAndUnknownExtension.sln"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText("CSharpProject_CSharpProject.csproj")));

            AssertThrows<InvalidOperationException>(() =>
            {
                var ws = MSBuildWorkspace.Create();
                ws.SkipUnrecognizedProjects = false;
                var solution = ws.OpenSolutionAsync(GetSolutionFileName(@"TestSolution.sln")).Result;
            },
            e =>
            {
                Assert.Equal(true, e.Message.Contains("extension"));
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithInvalidFilePath_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            AssertThrows<InvalidOperationException>(() =>
            {
                var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"http://localhost/Invalid/InvalidProject.csproj")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithNonExistentProjectFile_Fails()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            AssertThrows<FileNotFoundException>(() =>
            {
                var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"CSharpProject\NonExistentProject.csproj")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithInvalidProjectReference_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_InvalidProjectReference.vbproj")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to invalid file path.
            Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
            Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference 
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithInvalidProjectReference_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_InvalidProjectReference.vbproj")));

            AssertThrows<InvalidOperationException>(() =>
            {
                var ws = MSBuildWorkspace.Create();
                ws.SkipUnrecognizedProjects = false;
                var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithNonExistentProjectReference_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_NonExistentProjectReference.vbproj")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to invalid file path.
            Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
            Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference 
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithNonExistentProjectReference_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_NonExistentProjectReference.vbproj")));

            AssertThrows<FileNotFoundException>(() =>
            {
                var ws = MSBuildWorkspace.Create();
                ws.SkipUnrecognizedProjects = false;
                var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_SkipTrue_SucceedsWithEvent()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj")));

            var ws = MSBuildWorkspace.Create();

            var diagnostics = new List<WorkspaceDiagnostic>();
            ws.WorkspaceFailed += (s, args) =>
            {
                diagnostics.Add(args.Diagnostic);
            };

            var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            Assert.Equal(1, project.Solution.ProjectIds.Count); // didn't really open referenced project due to unrecognized extension.
            Assert.Equal(0, project.ProjectReferences.Count()); // no resolved project references
            Assert.Equal(1, project.AllProjectReferences.Count); // dangling project reference 
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_SkipFalse_Fails()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj")));

            AssertThrows<InvalidOperationException>(() =>
            {
                var ws = MSBuildWorkspace.Create();
                ws.SkipUnrecognizedProjects = false;
                var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_WithMetadata_SkipTrue_SucceedsByLoadingMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj"))
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            Assert.Equal(1, project.Solution.ProjectIds.Count);
            Assert.Equal(0, project.ProjectReferences.Count());
            Assert.Equal(0, project.AllProjectReferences.Count());

            var metaRefs = project.MetadataReferences.ToList();
            Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithUnrecognizedProjectReferenceFileExtension_WithMetadata_SkipFalse_SucceedsByLoadingMetadata()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText(@"VisualBasicProject_VisualBasicProject_UnknownProjectExtension.vbproj"))
                .WithFile(@"CSharpProject\CSharpProject.noproj", GetResourceText(@"CSharpProject_CSharpProject.csproj"))
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            var ws = MSBuildWorkspace.Create();
            ws.SkipUnrecognizedProjects = false;
            var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            Assert.Equal(1, project.Solution.ProjectIds.Count);
            Assert.Equal(0, project.ProjectReferences.Count());
            Assert.Equal(0, project.AllProjectReferences.Count());

            var metaRefs = project.MetadataReferences.ToList();
            Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithReferencedProject_LoadMetadata_ExistingMetadata_Succeeds()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            var ws = MSBuildWorkspace.Create();
            ws.LoadMetadataForReferencedProjects = true;
            var project = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            // referenced project got converted to a metadata reference
            var projRefs = project.ProjectReferences.ToList();
            var metaRefs = project.MetadataReferences.ToList();
            Assert.Equal(0, projRefs.Count);
            Assert.Equal(true, metaRefs.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithReferencedProject_LoadMetadata_NonExistentMetadata_LoadsProjectInstead()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            var ws = MSBuildWorkspace.Create();
            ws.LoadMetadataForReferencedProjects = true;
            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            // referenced project is still a project ref, did not get converted to metadata ref
            var projRefs = project.ProjectReferences.ToList();
            var metaRefs = project.MetadataReferences.ToList();
            Assert.Equal(1, projRefs.Count);
            Assert.False(metaRefs.Any(r => !r.Properties.Aliases.IsDefault && r.Properties.Aliases.Contains("CSharpProject")));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_UpdateExistingReferences()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"CSharpProject\bin\Debug\CSharpProject.dll", GetResourceBytes("CSharpProject.dll")));

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            // first open vb project that references c# project, but only reference the c# project's built metadata
            var ws = MSBuildWorkspace.Create();
            ws.LoadMetadataForReferencedProjects = true;
            var vbproject = ws.OpenProjectAsync(GetSolutionFileName(@"VisualBasicProject\VisualBasicProject.vbproj")).Result;

            // prove vb project references c# project as a metadata reference
            Assert.Equal(0, vbproject.ProjectReferences.Count());
            Assert.Equal(true, vbproject.MetadataReferences.Any(r => r is PortableExecutableReference && ((PortableExecutableReference)r).Display.Contains("CSharpProject.dll")));

            // now expliticly open the c# project that got referenced as metadata
            var csproject = ws.OpenProjectAsync(GetSolutionFileName(@"CSharpProject\CSharpProject.csproj")).Result;

            // show that the vb project now references the c# project directly (not as metadata)
            vbproject = ws.CurrentSolution.GetProject(vbproject.Id);
            Assert.Equal(1, vbproject.ProjectReferences.Count());
            Assert.False(vbproject.MetadataReferences.Any(r => !r.Properties.Aliases.IsDefault && r.Properties.Aliases.Contains("CSharpProject")));
        }

        [ConditionalFact(typeof(Framework35Installed))]
        [Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(528984, "DevDiv")]
        public void TestOpenProject_AddVBDefaultReferences()
        {
            string projectFile = "VisualBasicProject_VisualBasicProject_3_5.vbproj";
            CreateFiles(projectFile, "VisualBasicProject_VisualBasicClass.vb");

            // keep metadata reference from holding files open
            Workspace.TestHookStandaloneProjectsDoNotHoldReferences = true;

            var project = MSBuildWorkspace.Create().OpenProjectAsync(GetSolutionFileName(projectFile)).Result;
            var compilation = project.GetCompilationAsync().Result;
            var diagnostics = compilation.GetDiagnostics();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TTestCompilationOptions_CSharp_DebugType_Full()
        {
            CreateCSharpFilesWith("DebugType", "full");
            AssertOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_DebugType_None()
        {
            CreateCSharpFilesWith("DebugType", "none");
            AssertOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_DebugType_PDBOnly()
        {
            CreateCSharpFilesWith("DebugType", "pdbonly");
            AssertOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OutputKind_DynamicallyLinkedLibrary()
        {
            CreateCSharpFilesWith("OutputType", "Library");
            AssertOptions(OutputKind.DynamicallyLinkedLibrary, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OutputKind_ConsoleAppliaction()
        {
            CreateCSharpFilesWith("OutputType", "Exe");
            AssertOptions(OutputKind.ConsoleApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OutputKind_WindowsApplication()
        {
            CreateCSharpFilesWith("OutputType", "WinExe");
            AssertOptions(OutputKind.WindowsApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OutputKind_NetModule()
        {
            CreateCSharpFilesWith("OutputType", "Module");
            AssertOptions(OutputKind.NetModule, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OptimizationLevel_Release()
        {
            CreateCSharpFilesWith("Optimize", "True");
            AssertOptions(OptimizationLevel.Release, options => options.OptimizationLevel);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_OptimizationLevel_Debug()
        {
            CreateCSharpFilesWith("Optimize", "False");
            AssertOptions(OptimizationLevel.Debug, options => options.OptimizationLevel);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_MainFileName()
        {
            CreateCSharpFilesWith("StartupObject", "Foo");
            AssertOptions("Foo", options => options.MainTypeName);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_Missing()
        {
            CreateCSharpFiles();
            AssertOptions(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_False()
        {
            CreateCSharpFilesWith("SignAssembly", "false");
            AssertOptions(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_SignAssembly_True()
        {
            CreateCSharpFilesWith("SignAssembly", "true");
            AssertOptions("snKey.snk", options => Path.GetFileName(options.CryptoKeyFile));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_False()
        {
            CreateCSharpFilesWith("DelaySign", "false");
            AssertOptions(null, options => options.DelaySign);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_AssemblyOriginatorKeyFile_DelaySign_True()
        {
            CreateCSharpFilesWith("DelaySign", "true");
            AssertOptions(true, options => options.DelaySign);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_CheckOverflow_True()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "true");
            AssertOptions(true, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_CSharp_CheckOverflow_False()
        {
            CreateCSharpFilesWith("CheckForOverflowUnderflow", "false");
            AssertOptions(false, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_CSharp_Compatibility_ECMA1()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-1");
            AssertOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp1, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_CSharp_Compatibility_ECMA2()
        {
            CreateCSharpFilesWith("LangVersion", "ISO-2");
            AssertOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp2, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_CSharp_Compatibility_None()
        {
            CreateCSharpFilesWith("LangVersion", "3");
            AssertOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp3, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_CSharp_LanguageVersion_Latest()
        {
            CreateCSharpFiles();
            AssertOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp6, options => options.LanguageVersion);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_CSharp_PreprocessorSymbols()
        {
            CreateCSharpFilesWith("DefineConstants", "DEBUG;TRACE;X;Y");
            AssertOptions("DEBUG,TRACE,X,Y", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestConfigurationDebug()
        {
            CreateCSharpFiles();
            AssertOptions("DEBUG,TRACE", options => string.Join(",", options.PreprocessorSymbolNames));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestConfigurationRelease()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var sol = MSBuildWorkspace.Create(properties: new Dictionary<string, string> { { "Configuration", "Release" } })
                                      .OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.Projects.First();
            var options = project.ParseOptions;

            Assert.False(options.PreprocessorSymbolNames.Any(name => name == "DEBUG"), "DEBUG symbol not expected");
            Assert.True(options.PreprocessorSymbolNames.Any(name => name == "TRACE"), "TRACE symbol expected");
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_DebugType_Full()
        {
            CreateVBFilesWith("DebugType", "full");
            AssertVBOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_DebugType_None()
        {
            CreateVBFilesWith("DebugType", "none");
            AssertVBOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_DebugType_PDBOnly()
        {
            CreateVBFilesWith("DebugType", "pdbonly");
            AssertVBOptions(0, options => options.Errors.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_VBRuntime_Embed()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicProject.vbproj", GetResourceText("VisualBasicProject_VisualBasicProject_Embed.vbproj")));
            AssertVBOptions(true, options => options.EmbedVbCoreRuntime);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OutputKind_DynamicallyLinkedLibrary()
        {
            CreateVBFilesWith("OutputType", "Library");
            AssertVBOptions(OutputKind.DynamicallyLinkedLibrary, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OutputKind_ConsoleApplication()
        {
            CreateVBFilesWith("OutputType", "Exe");
            AssertVBOptions(OutputKind.ConsoleApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OutputKind_WindowsApplication()
        {
            CreateVBFilesWith("OutputType", "WinExe");
            AssertVBOptions(OutputKind.WindowsApplication, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OutputKind_NetModule()
        {
            CreateVBFilesWith("OutputType", "Module");
            AssertVBOptions(OutputKind.NetModule, options => options.OutputKind);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_RootNamespace()
        {
            CreateVBFilesWith("RootNamespace", "Foo.Bar");
            AssertVBOptions("Foo.Bar", options => options.RootNamespace);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionStrict_On()
        {
            CreateVBFilesWith("OptionStrict", "On");
            AssertVBOptions(Microsoft.CodeAnalysis.VisualBasic.OptionStrict.On, options => options.OptionStrict);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionStrict_Off()
        {
            CreateVBFilesWith("OptionStrict", "Off");
            AssertVBOptions(Microsoft.CodeAnalysis.VisualBasic.OptionStrict.Custom, options => options.OptionStrict);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionInfer_True()
        {
            CreateVBFilesWith("OptionInfer", "On");
            AssertVBOptions(true, options => options.OptionInfer);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionInfer_False()
        {
            CreateVBFilesWith("OptionInfer", "Off");
            AssertVBOptions(false, options => options.OptionInfer);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionExplicit_True()
        {
            CreateVBFilesWith("OptionExplicit", "On");
            AssertVBOptions(true, options => options.OptionExplicit);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionExplicit_False()
        {
            CreateVBFilesWith("OptionExplicit", "Off");
            AssertVBOptions(false, options => options.OptionExplicit);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionCompareText_True()
        {
            CreateVBFilesWith("OptionCompare", "Text");
            AssertVBOptions(true, options => options.OptionCompareText);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionCompareText_False()
        {
            CreateVBFilesWith("OptionCompare", "Binary");
            AssertVBOptions(false, options => options.OptionCompareText);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionRemoveIntegerOverflowChecks_True()
        {
            CreateVBFilesWith("RemoveIntegerChecks", "true");
            AssertVBOptions(false, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionRemoveIntegerOverflowChecks_False()
        {
            CreateVBFilesWith("RemoveIntegerChecks", "false");
            AssertVBOptions(true, options => options.CheckOverflow);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_OptionAssemblyOriginatorKeyFile_SignAssemblyFalse()
        {
            CreateVBFilesWith("SignAssembly", "false");
            AssertVBOptions(null, options => options.CryptoKeyFile);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestCompilationOptions_VisualBasic_GlobalImports()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
            var options = (Microsoft.CodeAnalysis.VisualBasic.VBCompilationOptions)project.CompilationOptions;
            var imports = options.GlobalImports;
            AssertEx.Equal(new[]
            {
                "Microsoft.VisualBasic",
                "System",
                "System.Collections",
                "System.Collections.Generic",
                "System.Diagnostics",
                "System.Linq",
            }, imports.Select(i => i.Name));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestParseOptions_VisualBasic_PreprocessorSymbols()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "DefineConstants", "X=1,Y=2,Z,T=-1,VBC_VER=123,F=false"));

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
            var options = (Microsoft.CodeAnalysis.VisualBasic.VBParseOptions)project.ParseOptions;
            var defines = new List<KeyValuePair<string, object>>(options.PreprocessorSymbols);
            defines.Sort((x, y) => x.Key.CompareTo(y.Key));

            AssertEx.Equal(new[]
            {
                new KeyValuePair<string, object>("_MyType", "Windows"),
                new KeyValuePair<string, object>("CONFIG", "Debug"),
                new KeyValuePair<string, object>("DEBUG", -1),
                new KeyValuePair<string, object>("F", false),
                new KeyValuePair<string, object>("PLATFORM", "AnyCPU"),
                new KeyValuePair<string, object>("T", -1),
                new KeyValuePair<string, object>("TARGET", "library"),
                new KeyValuePair<string, object>("TRACE", -1),
                new KeyValuePair<string, object>("VBC_VER", 123),
                new KeyValuePair<string, object>("X", 1),
                new KeyValuePair<string, object>("Y", 2),
                new KeyValuePair<string, object>("Z", true),
            }, defines);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void Test_VisualBasic_ConditionalAttributeEmitted()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicClass.vb", GetResourceText(@"VisualBasicProject_VisualBasicClass_WithConditionalAttributes.vb"))
                .ReplaceFileElement(@"VisualBasicProject\VisualBasicProject.vbproj", "DefineConstants", "EnableMyAttribute"));

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
            var options = (Microsoft.CodeAnalysis.VisualBasic.VBParseOptions)project.ParseOptions;
            Assert.Equal(true, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

            var compilation = project.GetCompilationAsync().Result;
            var metadataBytes = compilation.EmitToArray();
            var mtref = MetadataReference.CreateFromImage(metadataBytes);
            var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
            var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
            var attrs = sym.GetAttributes();

            Assert.Equal(true, attrs.Any(ad => ad.AttributeClass.Name == "MyAttribute"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void Test_VisualBasic_ConditionalAttributeNotEmitted()
        {
            CreateFiles(GetMultiProjectSolutionFiles()
                .WithFile(@"VisualBasicProject\VisualBasicClass.vb", GetResourceText(@"VisualBasicProject_VisualBasicClass_WithConditionalAttributes.vb")));

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("VisualBasicProject").FirstOrDefault();
            var options = (Microsoft.CodeAnalysis.VisualBasic.VBParseOptions)project.ParseOptions;
            Assert.Equal(false, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

            var compilation = project.GetCompilationAsync().Result;
            var metadataBytes = compilation.EmitToArray();
            var mtref = MetadataReference.CreateFromImage(metadataBytes);
            var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
            var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
            var attrs = sym.GetAttributes();

            Assert.Equal(false, attrs.Any(ad => ad.AttributeClass.Name == "MyAttribute"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void Test_CSharp_ConditionalAttributeEmitted()
        {
            CreateFiles(this.GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", GetResourceText(@"CSharpProject_CSharpClass_WithConditionalAttributes.cs"))
                .ReplaceFileElement(@"CSharpProject\CSharpProject.csproj", "DefineConstants", "EnableMyAttribute"));

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
            var options = project.ParseOptions;
            Assert.Equal(true, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

            var compilation = project.GetCompilationAsync().Result;
            var metadataBytes = compilation.EmitToArray();
            var mtref = MetadataReference.CreateFromImage(metadataBytes);
            var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
            var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
            var attrs = sym.GetAttributes();

            Assert.Equal(true, attrs.Any(ad => ad.AttributeClass.Name == "MyAttr"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void Test_CSharp_ConditionalAttributeNotEmitted()
        {
            CreateFiles(this.GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpClass.cs", GetResourceText(@"CSharpProject_CSharpClass_WithConditionalAttributes.cs")));

            var sol = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = sol.GetProjectsByName("CSharpProject").FirstOrDefault();
            var options = project.ParseOptions;
            Assert.Equal(false, options.PreprocessorSymbolNames.Contains("EnableMyAttribute"));

            var compilation = project.GetCompilationAsync().Result;
            var metadataBytes = compilation.EmitToArray();
            var mtref = MetadataReference.CreateFromImage(metadataBytes);
            var mtcomp = CS.CSharpCompilation.Create("MT", references: new MetadataReference[] { mtref });
            var sym = (IAssemblySymbol)mtcomp.GetAssemblyOrModuleSymbol(mtref);
            var attrs = sym.GetAttributes();

            Assert.Equal(false, attrs.Any(ad => ad.AttributeClass.Name == "MyAttr"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_CSharp_WithLinkedDocument()
        {
            var fooText = GetResourceText(@"OtherStuff_Foo.cs");

            CreateFiles(GetSimpleCSharpSolutionFiles()
                .WithFile(@"CSharpProject\CSharpProject.csproj", GetResourceText(@"CSharpProject_WithLink.csproj"))
                .WithFile(@"OtherStuff\Foo.cs", fooText));

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
            var documents = project.Documents.ToList();
            var fooDoc = documents.Single(d => d.Name == "Foo.cs");
            Assert.Equal(1, fooDoc.Folders.Count);
            Assert.Equal("Blah", fooDoc.Folders[0]);

            // prove that the file path is the correct full path to the actual file
            Assert.Equal(true, fooDoc.FilePath.Contains("OtherStuff"));
            Assert.Equal(true, File.Exists(fooDoc.FilePath));
            var text = File.ReadAllText(fooDoc.FilePath);
            Assert.Equal(fooText, text);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestAddDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();

            var newText = SourceText.From("public class Bar { }");
            ws.AddDocument(project.Id, new string[] { "NewFolder" }, "Bar.cs", newText);

            // check workspace current solution
            var solution2 = ws.CurrentSolution;
            var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
            var documents = project2.Documents.ToList();
            Assert.Equal(4, documents.Count);
            var document2 = documents.Single(d => d.Name == "Bar.cs");
            var text2 = document2.GetTextAsync().Result;
            Assert.Equal(newText.ToString(), text2.ToString());
            Assert.Equal(1, document2.Folders.Count);

            // check actual file on disk...
            var textOnDisk = File.ReadAllText(document2.FilePath);
            Assert.Equal(newText.ToString(), textOnDisk);

            // check project file on disk
            var projectFileText = File.ReadAllText(project2.FilePath);
            Assert.Equal(true, projectFileText.Contains(@"NewFolder\Bar.cs"));

            // reload project & solution to prove project file change was good
            var wsB = MSBuildWorkspace.Create();
            wsB.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Wait();
            var projectB = wsB.CurrentSolution.GetProjectsByName("CSharpProject").FirstOrDefault();
            var documentsB = projectB.Documents.ToList();
            Assert.Equal(4, documentsB.Count);
            var documentB = documentsB.Single(d => d.Name == "Bar.cs");
            Assert.Equal(1, documentB.Folders.Count);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestUpdateDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
            var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
            var originalText = document.GetTextAsync().Result;

            var newText = SourceText.From("public class Bar { }");
            ws.TryApplyChanges(solution.WithDocumentText(document.Id, newText, PreservationMode.PreserveIdentity));

            // check workspace current solution
            var solution2 = ws.CurrentSolution;
            var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
            var documents = project2.Documents.ToList();
            Assert.Equal(3, documents.Count);
            var document2 = documents.Single(d => d.Name == "CSharpClass.cs");
            var text2 = document2.GetTextAsync().Result;
            Assert.Equal(newText.ToString(), text2.ToString());

            // check actual file on disk...
            var textOnDisk = File.ReadAllText(document2.FilePath);
            Assert.Equal(newText.ToString(), textOnDisk);

            // check original text in original solution did not change
            Assert.Equal(originalText.ToString(), document.GetTextAsync().Result.ToString());
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestRemoveDocumentAsync()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
            var document = project.Documents.Single(d => d.Name == "CSharpClass.cs");
            var originalText = document.GetTextAsync().Result;

            ws.RemoveDocument(document.Id);

            // check workspace current solution
            var solution2 = ws.CurrentSolution;
            var project2 = solution2.GetProjectsByName("CSharpProject").FirstOrDefault();
            Assert.Equal(0, project2.Documents.Count(d => d.Name == "CSharpClass.cs"));

            // check actual file on disk...
            Assert.False(File.Exists(document.FilePath));

            // check original text in original solution did not change
            Assert.Equal(originalText.ToString(), document.GetTextAsync().Result.ToString());
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestApplyChanges_UpdateDocumentText()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var documents = solution.GetProjectsByName("CSharpProject").FirstOrDefault().Documents.ToList();
            var document = documents.Single(d => d.Name.Contains("CSharpClass"));
            var text = document.GetTextAsync().Result;
            var newText = SourceText.From("using System.Diagnostics;\r\n" + text.ToString());
            var newSolution = solution.WithDocumentText(document.Id, newText);

            ws.TryApplyChanges(newSolution);

            // check workspace current solution
            var solution2 = ws.CurrentSolution;
            var document2 = solution2.GetDocument(document.Id);
            var text2 = document2.GetTextAsync().Result;
            Assert.Equal(newText.ToString(), text2.ToString());

            // check actual file on disk...
            var textOnDisk = File.ReadAllText(document.FilePath);
            Assert.Equal(newText.ToString(), textOnDisk);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestApplyChanges_AddDocument()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            var ws = MSBuildWorkspace.Create();
            var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.GetProjectsByName("CSharpProject").FirstOrDefault();
            var newDocId = DocumentId.CreateNewId(project.Id);
            var newText = SourceText.From("public class Bar { }");
            var newSolution = solution.AddDocument(newDocId, "Bar.cs", newText);

            ws.TryApplyChanges(newSolution);

            // check workspace current solution
            var solution2 = ws.CurrentSolution;
            var document2 = solution2.GetDocument(newDocId);
            var text2 = document2.GetTextAsync().Result;
            Assert.Equal(newText.ToString(), text2.ToString());

            // check actual file on disk...
            var textOnDisk = File.ReadAllText(document2.FilePath);
            Assert.Equal(newText.ToString(), textOnDisk);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestApplyChanges_NotSupportedChangesFail()
        {
#if !MSBUILD12
            var csharpProjPath = @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj";
            var vbProjPath = @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj";
            CreateFiles(GetAnalyzerReferenceSolutionFiles());

            var ws = MSBuildWorkspace.Create();

            var cspid = ws.OpenProjectAsync(GetSolutionFileName(csharpProjPath)).Result.Id;
            var vbpid = ws.OpenProjectAsync(GetSolutionFileName(vbProjPath)).Result.Id;

            // adding additional documents not supported.
            Assert.Equal(false, ws.CanApplyChange(ApplyChangesKind.AddAdditionalDocument));
            Assert.Throws<NotSupportedException>(delegate
            {
                ws.TryApplyChanges(ws.CurrentSolution.AddAdditionalDocument(DocumentId.CreateNewId(cspid), "foo.xaml", SourceText.From("<foo></foo>")));
            });

            var xaml = ws.CurrentSolution.GetProject(cspid).AdditionalDocuments.FirstOrDefault(d => d.Name == "XamlFile.xaml");
            Assert.NotNull(xaml);

            // removing additional documents not supported
            Assert.Equal(false, ws.CanApplyChange(ApplyChangesKind.RemoveAdditionalDocument));
            Assert.Throws<NotSupportedException>(delegate
            {
                ws.TryApplyChanges(ws.CurrentSolution.RemoveAdditionalDocument(xaml.Id));
            });

#if false // No current text changing API's for additional documents
            // chanding additional documents not supported
            Assert.Throws<NotSupportedException>(delegate
            {
            });
#endif
            // adding analyzer references is not supported.
            Assert.Equal(false, ws.CanApplyChange(ApplyChangesKind.AddAnalyzerReference));
            Assert.Throws<NotSupportedException>(delegate
            {
                var p = ws.CurrentSolution.GetProject(cspid);
                var a = new AnalyzerFileReference(GetSolutionFileName("myAnalyzer.dll"));
                ws.TryApplyChanges(p.AddAnalyzerReference(a).Solution);
            });

            // removing analyzer references is not supported.
            Assert.Equal(false, ws.CanApplyChange(ApplyChangesKind.RemoveAnalyzerReference));
            Assert.Throws<NotSupportedException>(delegate
            {
                var p = ws.CurrentSolution.GetProject(cspid);
                var a = p.AnalyzerReferences.First();
                ws.TryApplyChanges(p.RemoveAnalyzerReference(a).Solution);
            });

            // adding project references is not supported
            Assert.Equal(false, ws.CanApplyChange(ApplyChangesKind.AddProjectReference));
            Assert.Throws<NotSupportedException>(delegate
            {
                var p = ws.CurrentSolution.GetProject(cspid);
                ws.TryApplyChanges(p.AddProjectReference(new ProjectReference(vbpid)).Solution);
            });
#endif
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestWorkspaceChangedEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Wait();
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = ws.CurrentSolution;

                using (var wew = ws.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = ws.CurrentSolution.Projects.First().Documents.First();
                    var newText = "/* new text */\r\n" + doc.GetTextAsync().Result.ToString();

                    ws.TryApplyChanges(ws.CurrentSolution.WithDocumentText(doc.Id, SourceText.From(newText), PreservationMode.PreserveIdentity));

                    Assert.True(wew.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestWorkspaceChangedWeakEvent()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Wait();
                var expectedEventKind = WorkspaceChangeKind.DocumentChanged;
                var originalSolution = ws.CurrentSolution;

                using (var wew = ws.VerifyWorkspaceChangedEvent(args =>
                {
                    Assert.Equal(expectedEventKind, args.Kind);
                    Assert.NotNull(args.NewSolution);
                    Assert.NotSame(originalSolution, args.NewSolution);
                }))
                {
                    // change document text (should fire SolutionChanged event)
                    var doc = ws.CurrentSolution.Projects.First().Documents.First();
                    var newText = "/* new text */\r\n" + doc.GetTextAsync().Result.ToString();

                    ws.TryApplyChanges(
                        ws
                        .CurrentSolution
                        .WithDocumentText(
                            doc.Id,
                            SourceText.From(newText),
                            PreservationMode.PreserveIdentity));

                    Assert.True(wew.WaitForEventToFire(AsyncEventTimeout),
                        string.Format("event {0} was not fired within {1}",
                        Enum.GetName(typeof(WorkspaceChangeKind), expectedEventKind),
                        AsyncEventTimeout));
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestSemanticVersionCS()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;

            var csprojectId = solution.Projects.First(p => p.Language == LanguageNames.CSharp).Id;
            var csdoc1 = solution.GetProject(csprojectId).Documents.Single(d => d.Name == "CSharpClass.cs");

            // add method
            var startOfClassInterior = csdoc1.GetSyntaxRootAsync().Result.DescendantNodes().OfType<CS.Syntax.ClassDeclarationSyntax>().First().OpenBraceToken.FullSpan.End;
            var csdoc2 = AssertSemanticVersionChanged(csdoc1, csdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "public void M() {\r\n}\r\n"));

            // change interior of method
            var startOfMethodInterior = csdoc2.GetSyntaxRootAsync().Result.DescendantNodes().OfType<CS.Syntax.MethodDeclarationSyntax>().First().Body.OpenBraceToken.FullSpan.End;
            var csdoc3 = AssertSemanticVersionUnchanged(csdoc2, csdoc2.GetTextAsync().Result.Replace(new TextSpan(startOfMethodInterior, 0), "int x = 10;\r\n"));

            // add whitespace outside of method
            var csdoc4 = AssertSemanticVersionUnchanged(csdoc3, csdoc3.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\n\r\n   \r\n"));

            // add field with initializer
            var csdoc5 = AssertSemanticVersionChanged(csdoc1, csdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic int X = 20;\r\n"));

            // change initializer value 
            var literal = csdoc5.GetSyntaxRootAsync().Result.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
            var csdoc6 = AssertSemanticVersionUnchanged(csdoc5, csdoc5.GetTextAsync().Result.Replace(literal.Span, "100"));

            // add const field with initializer
            var csdoc7 = AssertSemanticVersionChanged(csdoc1, csdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\npublic const int X = 20;\r\n"));

            // change constant initializer value
            var literal7 = csdoc7.GetSyntaxRootAsync().Result.DescendantNodes().OfType<CS.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
            var csdoc8 = AssertSemanticVersionChanged(csdoc7, csdoc7.GetTextAsync().Result.Replace(literal7.Span, "100"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestSemanticVersionVB()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;

            var vbprojectId = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic).Id;
            var vbdoc1 = solution.GetProject(vbprojectId).Documents.Single(d => d.Name == "VisualBasicClass.vb");

            // add method
            var startOfClassInterior = GetMethodInsertionPoint(vbdoc1.GetSyntaxRootAsync().Result.DescendantNodes().OfType<VB.Syntax.ClassBlockSyntax>().First());
            var vbdoc2 = AssertSemanticVersionChanged(vbdoc1, vbdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic Sub M()\r\n\r\nEnd Sub\r\n"));

            // change interior of method
            var startOfMethodInterior = vbdoc2.GetSyntaxRootAsync().Result.DescendantNodes().OfType<VB.Syntax.MethodBlockBaseSyntax>().First().Begin.FullSpan.End;
            var vbdoc3 = AssertSemanticVersionUnchanged(vbdoc2, vbdoc2.GetTextAsync().Result.Replace(new TextSpan(startOfMethodInterior, 0), "\r\nDim x As Integer = 10\r\n"));

            // add whitespace outside of method
            var vbdoc4 = AssertSemanticVersionUnchanged(vbdoc3, vbdoc3.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\n\r\n   \r\n"));

            // add field with initializer
            var vbdoc5 = AssertSemanticVersionChanged(vbdoc1, vbdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic X As Integer = 20;\r\n"));

            // change initializer value
            var literal = vbdoc5.GetSyntaxRootAsync().Result.DescendantNodes().OfType<VB.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
            var vbdoc6 = AssertSemanticVersionUnchanged(vbdoc5, vbdoc5.GetTextAsync().Result.Replace(literal.Span, "100"));

            // add const field with initializer
            var vbdoc7 = AssertSemanticVersionChanged(vbdoc1, vbdoc1.GetTextAsync().Result.Replace(new TextSpan(startOfClassInterior, 0), "\r\nPublic Const X As Integer = 20;\r\n"));

            // change constant initializer value
            var literal7 = vbdoc7.GetSyntaxRootAsync().Result.DescendantNodes().OfType<VB.Syntax.LiteralExpressionSyntax>().First(x => x.Token.ValueText == "20");
            var vbdoc8 = AssertSemanticVersionChanged(vbdoc7, vbdoc7.GetTextAsync().Result.Replace(literal7.Span, "100"));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace), WorkItem(529276, "DevDiv"), WorkItem(12086, "DevDiv_Projects/Roslyn")]
        public void TestOpenProject_LoadMetadataForReferenceProjects_NoMetadata()
        {
            var projPath = @"CSharpProject\CSharpProject_ProjectReference.csproj";
            var files = GetProjectReferenceSolutionFiles();

            CreateFiles(files);

            var projectFullPath = Path.Combine(this.SolutionDirectory.Path, projPath);
            using (var ws = MSBuildWorkspace.Create())
            {
                ws.LoadMetadataForReferencedProjects = true;
                var proj = ws.OpenProjectAsync(projectFullPath).Result;

                // prove that project gets opened instead.
                Assert.Equal(2, ws.CurrentSolution.Projects.Count());

                // and all is well
                var comp = proj.GetCompilationAsync().Result;
                var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
                Assert.Empty(errs);
            }
        }

        [WorkItem(918072, "DevDiv")]
        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestAnalyzerReferenceLoadStandalone()
        {
#if !MSBUILD12
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var ws = MSBuildWorkspace.Create())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = Path.Combine(this.SolutionDirectory.Path, projectPath);
                    var proj = ws.OpenProjectAsync(projectFullPath).Result;
                    Assert.Equal(1, proj.AnalyzerReferences.Count);
                    var analyzerReference = proj.AnalyzerReferences.First() as AnalyzerFileReference;
                    Assert.NotNull(analyzerReference);
                    Assert.True(analyzerReference.FullPath.EndsWith("CSharpProject.dll"));
                }

                // prove that project gets opened instead.
                Assert.Equal(2, ws.CurrentSolution.Projects.Count());
            }
#endif
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestAdditionalFilesStandalone()
        {
#if !MSBUILD12
            var projPaths = new[] { @"AnalyzerSolution\CSharpProject_AnalyzerReference.csproj", @"AnalyzerSolution\VisualBasicProject_AnalyzerReference.vbproj" };
            var files = GetAnalyzerReferenceSolutionFiles();

            CreateFiles(files);

            using (var ws = MSBuildWorkspace.Create())
            {
                foreach (var projectPath in projPaths)
                {
                    var projectFullPath = Path.Combine(this.SolutionDirectory.Path, projectPath);
                    var proj = ws.OpenProjectAsync(projectFullPath).Result;
                    Assert.Equal(1, proj.AdditionalDocuments.Count());
                    var doc = proj.AdditionalDocuments.First();
                    Assert.Equal("XamlFile.xaml", doc.Name);
                    Assert.Contains("Window", doc.GetTextAsync().WaitAndGetResult(CancellationToken.None).ToString());
                }
            }
#endif
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace), WorkItem(546171, "DevDiv")]
        public void TestCSharpExternAlias()
        {
            var projPath = @"CSharpProject\CSharpProject_ExternAlias.csproj";
            var files = new FileSet(new Dictionary<string, object>
            {
                { projPath, GetResourceText("CSharpProject_CSharpProject_ExternAlias.csproj") },
                { @"CSharpProject\CSharpExternAlias.cs", GetResourceText("CSharpProject_CSharpExternAlias.cs") },
            });

            CreateFiles(files);

            var fullPath = Path.Combine(this.SolutionDirectory.Path, projPath);
            using (var ws = MSBuildWorkspace.Create())
            {
                var proj = ws.OpenProjectAsync(fullPath).Result;
                var comp = proj.GetCompilationAsync().Result;
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace), WorkItem(530337, "DevDiv")]
        public void TestProjectReferenceWithExternAlias()
        {
            var files = GetProjectReferenceSolutionFiles();
            CreateFiles(files);

            var fullPath = Path.Combine(this.SolutionDirectory.Path, @"CSharpProjectReference.sln");
            using (var ws = MSBuildWorkspace.Create())
            {
                var sol = ws.OpenSolutionAsync(fullPath).Result;
                var proj = sol.Projects.First();
                var comp = proj.GetCompilationAsync().Result;
                comp.GetDiagnostics().Where(d => d.Severity > DiagnosticSeverity.Info).Verify();
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestProjectReferenceWithReferenceOutputAssemblyFalse()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Add(new XElement(XName.Get("ReferenceOutputAssembly", MSBuildNamespace), "false"));
            });

            CreateFiles(files);

            var fullPath = Path.Combine(this.SolutionDirectory.Path, @"CSharpProjectReference.sln");
            using (var ws = MSBuildWorkspace.Create())
            {
                var sol = ws.OpenSolutionAsync(fullPath).Result;
                foreach (var project in sol.Projects)
                {
                    Assert.Equal(0, project.ProjectReferences.Count());
                }
            }
        }

        private FileSet VisitProjectReferences(FileSet files, Action<XElement> visitProjectReference)
        {
            var result = new List<KeyValuePair<string, object>>();
            foreach (var file in files)
            {
                string text = file.Value.ToString();
                if (file.Key.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                {
                    text = VisitProjectReferences(text, visitProjectReference);
                }

                result.Add(new KeyValuePair<string, object>(file.Key, text));
            }

            return new FileSet(result);
        }

        private string VisitProjectReferences(string projectFileText, Action<XElement> visitProjectReference)
        {
            var document = XDocument.Parse(projectFileText);
            var projectReferenceItems = document.Descendants(XName.Get("ProjectReference", MSBuildNamespace));
            foreach (var projectReferenceItem in projectReferenceItems)
            {
                visitProjectReference(projectReferenceItem);
            }

            return document.ToString();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestProjectReferenceWithNoGuid()
        {
            var files = GetProjectReferenceSolutionFiles();
            files = VisitProjectReferences(files, r =>
            {
                r.Elements(XName.Get("Project", MSBuildNamespace)).Remove();
            });

            CreateFiles(files);

            var fullPath = Path.Combine(this.SolutionDirectory.Path, @"CSharpProjectReference.sln");
            using (var ws = MSBuildWorkspace.Create())
            {
                var sol = ws.OpenSolutionAsync(fullPath).Result;
                foreach (var project in sol.Projects)
                {
                    Assert.InRange(project.ProjectReferences.Count(), 0, 1);
                }
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_MetadataReferenceHasDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
                var project = solution.Projects.First();
                var comp = project.GetCompilationAsync().Result;
                var symbol = comp.GetTypeByMetadataName("System.Console");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_CSharp_HasSourceDocComments()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
                var project = solution.Projects.First();
                var parseOptions = (CS.CSharpParseOptions)project.ParseOptions;
                Assert.Equal(DocumentationMode.Parse, parseOptions.DocumentationMode);
                var comp = project.GetCompilationAsync().Result;
                var symbol = comp.GetTypeByMetadataName("CSharpProject.CSharpClass");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_VisualBasic_HasSourceDocComments()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
                var project = solution.Projects.First(p => p.Language == LanguageNames.VisualBasic);
                var parseOptions = (VB.VBParseOptions)project.ParseOptions;
                Assert.Equal(DocumentationMode.Diagnose, parseOptions.DocumentationMode);
                var comp = project.GetCompilationAsync().Result;
                var symbol = comp.GetTypeByMetadataName("VisualBasicProject.VisualBasicClass");
                var docComment = symbol.GetDocumentationCommentXml();
                Assert.NotNull(docComment);
                Assert.NotEqual(0, docComment.Length);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_CrossLanguageSkeletonReferenceHasDocComments()
        {
            CreateFiles(GetMultiProjectSolutionFiles());

            using (var ws = MSBuildWorkspace.Create())
            {
                var solution = ws.OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
                var csproject = ws.CurrentSolution.Projects.First(p => p.Language == LanguageNames.CSharp);
                var csoptions = (CS.CSharpParseOptions)csproject.ParseOptions;
                Assert.Equal(DocumentationMode.Parse, csoptions.DocumentationMode);
                var cscomp = csproject.GetCompilationAsync().Result;
                var cssymbol = cscomp.GetTypeByMetadataName("CSharpProject.CSharpClass");
                var cscomment = cssymbol.GetDocumentationCommentXml();
                Assert.NotNull(cscomment);

                var vbproject = ws.CurrentSolution.Projects.First(p => p.Language == LanguageNames.VisualBasic);
                var vboptions = (VB.VBParseOptions)vbproject.ParseOptions;
                Assert.Equal(DocumentationMode.Diagnose, vboptions.DocumentationMode);
                var vbcomp = vbproject.GetCompilationAsync().Result;
                var vbsymbol = vbcomp.GetTypeByMetadataName("VisualBasicProject.VisualBasicClass");
                var parent = vbsymbol.BaseType; // this is the vb imported version of the csharp symbol
                var vbcomment = parent.GetDocumentationCommentXml();

                Assert.Equal(cscomment, vbcomment);
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithProjectFileLocked()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\CSharpProject.csproj");
            using (File.Open(projectFile, FileMode.Open, FileAccess.ReadWrite))
            {
                var ws = MSBuildWorkspace.Create();
                AssertThrows<System.IO.IOException>(() =>
                {
                    ws.OpenProjectAsync(projectFile).Wait();
                });
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenProject_WithNonExistentProjectFile()
                {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no one else can read
            var projectFile = GetSolutionFileName(@"CSharpProject\NoProject.csproj");
            var ws = MSBuildWorkspace.Create();
            AssertThrows<System.IO.FileNotFoundException>(() =>
                {
                ws.OpenProjectAsync(projectFile).Wait();
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_WithNonExistentSolutionFile()
        {
            CreateFiles(GetSimpleCSharpSolutionFiles());

            // open for read-write so no one else can read
            var solutionFile = GetSolutionFileName(@"NoSolution.sln");
            var ws = MSBuildWorkspace.Create();
            AssertThrows<System.IO.FileNotFoundException>(() =>
            {
                ws.OpenSolutionAsync(solutionFile).Wait();
            });
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        public void TestOpenSolution_SolutionFileHasEmptyLinesAndWhitespaceOnlyLines()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution.sln", GetResourceText("TestSolution_CSharp_EmptyLines.sln") },
                { @"CSharpProject\CSharpProject.csproj", GetResourceText("CSharpProject_CSharpProject.csproj") },
                { @"CSharpProject\CSharpClass.cs", GetResourceText("CSharpProject_CSharpClass.cs") },
                { @"CSharpProject\Properties\AssemblyInfo.cs", GetResourceText("CSharpProject_AssemblyInfo.cs") }
            });

            CreateFiles(files);

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
            var project = solution.Projects.First();
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(531543, "DevDiv")]
        public void TestOpenSolution_SolutionFileHasEmptyLineBetweenProjectBlock()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution.sln", GetResourceText("TestLoad_SolutionFileWithEmptyLineBetweenProjectBlock.sln") }
            });

            CreateFiles(files);

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution.sln")).Result;
        }

        [Fact(Skip = "531283"), Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(531283, "DevDiv")]
        public void TestOpenSolution_SolutionFileHasMissingEndProject()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"TestSolution1.sln", GetResourceText("TestSolution_MissingEndProject1.sln") },
                { @"TestSolution2.sln", GetResourceText("TestSolution_MissingEndProject2.sln") },
                { @"TestSolution3.sln", GetResourceText("TestSolution_MissingEndProject3.sln") }
            });

            CreateFiles(files);

            var solution1 = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution1.sln")).Result;
            var solution2 = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution2.sln")).Result;
            var solution3 = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("TestSolution3.sln")).Result;
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(792912, "DevDiv")]
        public void TestOpenSolution_WithDuplicatedGuidsBecomeSelfReferential()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"DuplicatedGuids.sln", GetResourceText("TestSolution_DuplicatedGuidsBecomeSelfReferential.sln") },
                { @"ReferenceTest\ReferenceTest.csproj", GetResourceText("CSharpProject_DuplicatedGuidsBecomeSelfReferential.csproj") },
                { @"Library1\Library1.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary1.csproj") },
            });

            CreateFiles(files);

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("DuplicatedGuids.sln")).Result;
            foreach (var p in solution.Projects)
            {
                var c = p.GetCompilationAsync().Result;
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(792912, "DevDiv")]
        public void TestOpenSolution_WithDuplicatedGuidsBecomeCircularReferential()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { @"DuplicatedGuids.sln", GetResourceText("TestSolution_DuplicatedGuidsBecomeCircularReferential.sln") },
                { @"ReferenceTest\ReferenceTest.csproj", GetResourceText("CSharpProject_DuplicatedGuidsBecomeCircularReferential.csproj") },
                { @"Library1\Library1.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary3.csproj") },
                { @"Library2\Library2.csproj", GetResourceText("CSharpProject_DuplicatedGuidLibrary4.csproj") },
            });

            CreateFiles(files);

            var solution = MSBuildWorkspace.Create().OpenSolutionAsync(GetSolutionFileName("DuplicatedGuids.sln")).Result;
            foreach (var p in solution.Projects)
            {
                var c = p.GetCompilationAsync().Result;
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(991528)]
        public void MSBuildProjectShouldHandleCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>1254</CodePage>") },
                { "class1.cs", "//“" }
            });

            CreateFiles(files);

            var projPath = Path.Combine(this.SolutionDirectory.Path, "Encoding.csproj");
            var project = MSBuildWorkspace.Create().OpenProjectAsync(projPath).Result;

            var text = project.Documents.First(d => d.Name == "class1.cs").GetTextAsync().Result;
            Assert.Equal(Encoding.GetEncoding(1254), text.Encoding);

            // The smart quote (“) in class1.cs shows up as "â€œ" in codepage 1254. Do a sanity
            // check here to make sure this file hasn't been corrupted in a way that would
            // impact subsequent asserts.
            Assert.Equal("//â€œ".Length, 5);
            Assert.Equal("//â€œ".Length, text.Length);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(991528)]
        public void MSBuildProjectShouldHandleInvalidCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>-1</CodePage>") },
                { "class1.cs", "//“" }
            });

            CreateFiles(files);

            var projPath = Path.Combine(this.SolutionDirectory.Path, "Encoding.csproj");

            var project = MSBuildWorkspace.Create().OpenProjectAsync(projPath).Result;

            Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), project.Documents.First(d => d.Name == "class1.cs").GetTextAsync().Result.Encoding);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(991528)]
        public void MSBuildProjectShouldHandleInvalidCodePageProperty2()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", "<CodePage>Broken</CodePage>") },
                { "class1.cs", "//“" }
            });

            CreateFiles(files);

            var projPath = Path.Combine(this.SolutionDirectory.Path, "Encoding.csproj");

            var project = MSBuildWorkspace.Create().OpenProjectAsync(projPath).Result;

            Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), project.Documents.First(d => d.Name == "class1.cs").GetTextAsync().Result.Encoding);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.Workspace)]
        [WorkItem(991528)]
        public void MSBuildProjectShouldHandleDefaultCodePageProperty()
        {
            var files = new FileSet(new Dictionary<string, object>
            {
                { "Encoding.csproj", GetResourceText("Encoding.csproj").Replace("<CodePage>ReplaceMe</CodePage>", string.Empty) },
                { "class1.cs", "//“" }
            });

            CreateFiles(files);

            var projPath = Path.Combine(this.SolutionDirectory.Path, "Encoding.csproj");
            var project = MSBuildWorkspace.Create().OpenProjectAsync(projPath).Result;

            var text = project.Documents.First(d => d.Name == "class1.cs").GetTextAsync().Result;
            Assert.Equal(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), text.Encoding);
            Assert.Equal("//“", text.ToString());
        }
    }
}
