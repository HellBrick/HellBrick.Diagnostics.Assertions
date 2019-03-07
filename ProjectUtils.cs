using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace HellBrick.Diagnostics.Assertions
{
	internal static class ProjectUtils
	{
		private static readonly MetadataReference _corlibReference = MetadataReference.CreateFromFile( typeof( object ).Assembly.Location );
		private static readonly MetadataReference _systemCoreReference = MetadataReference.CreateFromFile( typeof( Enumerable ).Assembly.Location );
		private static readonly MetadataReference _cSharpSymbolsReference = MetadataReference.CreateFromFile( typeof( CSharpCompilation ).Assembly.Location );
		private static readonly MetadataReference _codeAnalysisReference = MetadataReference.CreateFromFile( typeof( Compilation ).Assembly.Location );

		private static readonly string _defaultFilePathPrefix = "Test";
		private static readonly string _cSharpDefaultFileExt = "cs";
		private static readonly string _testProjectName = "TestProject";

		public static Project CreateProject( string[] sources, Func<OptionSet, OptionSet> optionConfigurator = null, Func<CSharpParseOptions, CSharpParseOptions> parseOptionsConfigurator = null )
		{
			optionConfigurator = optionConfigurator ?? ( o => o.WithProperFormatting() );
			parseOptionsConfigurator = parseOptionsConfigurator ?? ( o => o );
			string fileNamePrefix = _defaultFilePathPrefix;
			string fileExt = _cSharpDefaultFileExt;

			ProjectId projectId = ProjectId.CreateNewId( debugName: _testProjectName );

			AdhocWorkspace workspace = new AdhocWorkspace();
			workspace.Options = optionConfigurator( workspace.Options );

			Solution solution = workspace
				.CurrentSolution
				.AddProject( projectId, _testProjectName, _testProjectName, LanguageNames.CSharp )
				.AddMetadataReference( projectId, _corlibReference )
				.AddMetadataReference( projectId, _systemCoreReference )
				.AddMetadataReference( projectId, _cSharpSymbolsReference )
				.AddMetadataReference( projectId, _codeAnalysisReference );

			int count = 0;
			foreach ( string source in sources )
			{
				string newFileName = fileNamePrefix + count + "." + fileExt;
				DocumentId documentId = DocumentId.CreateNewId( projectId, debugName: newFileName );
				solution = solution.AddDocument( documentId, newFileName, SourceText.From( source ) );
				count++;
			}
			Project project = solution.GetProject( projectId );
			CSharpParseOptions defaultParseOptions = ( (CSharpParseOptions) project.ParseOptions ).WithLanguageVersion( LanguageVersion.Latest );
			CSharpParseOptions parseOptions = parseOptionsConfigurator( defaultParseOptions );
			return project.WithParseOptions( parseOptions );
		}

		public static Diagnostic[] GetSortedDiagnosticsFromDocuments( DiagnosticAnalyzer analyzer, Document[] documents )
		{
			HashSet<Project> projects = new HashSet<Project>();
			foreach ( Document document in documents )
			{
				projects.Add( document.Project );
			}

			List<Diagnostic> diagnostics = new List<Diagnostic>();
			foreach ( Project project in projects )
			{
				CompilationWithAnalyzers compilationWithAnalyzers = project.GetCompilationAsync().Result.WithAnalyzers( ImmutableArray.Create( analyzer ) );
				ImmutableArray<Diagnostic> diags = compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().Result;
				foreach ( Diagnostic diag in diags )
				{
					if ( diag.Location == Location.None || diag.Location.IsInMetadata )
					{
						diagnostics.Add( diag );
					}
					else
					{
						for ( int i = 0; i < documents.Length; i++ )
						{
							Document document = documents[ i ];
							SyntaxTree tree = document.GetSyntaxTreeAsync().Result;
							if ( tree == diag.Location.SourceTree )
							{
								diagnostics.Add( diag );
							}
						}
					}
				}
			}

			Diagnostic[] results = SortDiagnostics( diagnostics );
			diagnostics.Clear();
			return results;
		}

		private static Diagnostic[] SortDiagnostics( IEnumerable<Diagnostic> diagnostics )
			=> diagnostics
			.OrderBy( d => d.Location.SourceSpan.Start )
			.ToArray();
	}
}
