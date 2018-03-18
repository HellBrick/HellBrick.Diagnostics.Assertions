using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace HellBrick.Diagnostics.Assertions
{
	public static class AnalyzerVerifier
	{
		public static AnalyzerVerifier<TAnalyzer> UseAnalyzer<TAnalyzer>()
			where TAnalyzer : DiagnosticAnalyzer, new()
			=> default;
	}

	public readonly struct AnalyzerVerifier<TAnalyzer>
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		public AnalyzerVerifier<TAnalyzer, TCodeFix> UseCodeFix<TCodeFix>()
			where TCodeFix : CodeFixProvider, new()
			=> default;
	}

	public readonly struct AnalyzerVerifier<TAnalyzer, TCodeFix>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TCodeFix : CodeFixProvider, new()
	{
		public AnalyzerVerifier<TAnalyzer, TCodeFix, string, SingleSourceCollectionFactory> Source( string source )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix, string, SingleSourceCollectionFactory>( source );

		public AnalyzerVerifier<TAnalyzer, TCodeFix, string[], MultiSourceCollectionFactory> Sources( params string[] sources )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix, string[], MultiSourceCollectionFactory>( sources );
	}

	public readonly struct AnalyzerVerifier<TAnalyzer, TCodeFix, TSource, TSourceCollectionFactory>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TCodeFix : CodeFixProvider, new()
		where TSourceCollectionFactory : struct, ISourceCollectionFactory<TSource>
	{
		private static readonly MetadataReference _corlibReference = MetadataReference.CreateFromFile( typeof( object ).Assembly.Location );
		private static readonly MetadataReference _systemCoreReference = MetadataReference.CreateFromFile( typeof( Enumerable ).Assembly.Location );
		private static readonly MetadataReference _cSharpSymbolsReference = MetadataReference.CreateFromFile( typeof( CSharpCompilation ).Assembly.Location );
		private static readonly MetadataReference _codeAnalysisReference = MetadataReference.CreateFromFile( typeof( Compilation ).Assembly.Location );

		private static string _defaultFilePathPrefix = "Test";
		private static string _cSharpDefaultFileExt = "cs";
		private static string _cSharpDefaultFilePath = _defaultFilePathPrefix + 0 + "." + _cSharpDefaultFileExt;
		private static string _testProjectName = "TestProject";

		private readonly TSource _sources;

		public AnalyzerVerifier( TSource sources ) => _sources = sources;

		public void ShouldHaveNoDiagnostics() => VerifyNoFix( default( TSourceCollectionFactory ).CreateCollection( _sources ) );

		public AnalyzerVerifier<TAnalyzer, TCodeFix, TSource, TSourceCollectionFactory> ShouldHaveFix( TSource fixedSources )
			=> ShouldHaveFix( codeFixIndex: null, fixedSources );

		public AnalyzerVerifier<TAnalyzer, TCodeFix, TSource, TSourceCollectionFactory> ShouldHaveFix( int codeFixIndex, TSource fixedSources )
			=> ShouldHaveFix( new int?( codeFixIndex ), fixedSources );

		private AnalyzerVerifier<TAnalyzer, TCodeFix, TSource, TSourceCollectionFactory> ShouldHaveFix( int? codeFixIndex, TSource fixedSources )
		{
			VerifyCSharpFix
			(
				default( TSourceCollectionFactory ).CreateCollection( _sources ),
				default( TSourceCollectionFactory ).CreateCollection( fixedSources ),
				codeFixIndex
			);
			return this;
		}

		private void VerifyNoFix( string[] sources ) => VerifyCSharpFix( sources, sources, codeFixIndex: null );
		private void VerifyCSharpFix( string[] sources, string[] fixedSources, int? codeFixIndex )
			=> VerifyFix( new TAnalyzer(), new TCodeFix(), sources, fixedSources, codeFixIndex );

		private void VerifyFix( DiagnosticAnalyzer analyzer, CodeFixProvider codeFixProvider, string[] oldSources, string[] newSources, int? codeFixIndex )
		{
			Project project = CreateProject( oldSources );
			Document[] documents = project.Documents.ToArray();
			Diagnostic[] analyzerDiagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( documents );
			for ( int documentIndex = 0; documentIndex < documents.Length; documentIndex++ )
			{
				Document document = documents[ documentIndex ];
				string newSource = newSources[ documentIndex ];

				IEnumerable<Diagnostic> compilerDiagnostics = GetCompilerDiagnostics( document );
				int attempts = analyzerDiagnostics.Length;

				for ( int i = 0; i < attempts; ++i )
				{
					List<CodeAction> actions = new List<CodeAction>();
					TextSpan span = analyzerDiagnostics[ 0 ].Location.SourceSpan;
					ImmutableArray<Diagnostic> spanDiagnostics = ImmutableArray.Create( analyzerDiagnostics.Where( d => d.Location.SourceSpan == span ).ToArray() );
					CodeFixContext context = new CodeFixContext( document, span, spanDiagnostics, ( a, d ) => actions.Add( a ), CancellationToken.None );
					codeFixProvider.RegisterCodeFixesAsync( context ).Wait();

					if ( !actions.Any() )
					{
						break;
					}

					if ( codeFixIndex != null )
					{
						document = ApplyFix( document, actions.ElementAt( (int) codeFixIndex ) );
						break;
					}

					document = ApplyFix( document, actions.ElementAt( 0 ) );
					analyzerDiagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( document );

					IEnumerable<Diagnostic> newCompilerDiagnostics = GetNewDiagnostics( compilerDiagnostics, GetCompilerDiagnostics( document ) );

					//check if applying the code fix introduced any new compiler diagnostics
					if ( newCompilerDiagnostics.Any() )
					{
						// Format and get the compiler diagnostics again so that the locations make sense in the output
						document = document.WithSyntaxRoot( Formatter.Format( document.GetSyntaxRootAsync().Result, Formatter.Annotation, document.Project.Solution.Workspace ) );
						newCompilerDiagnostics = GetNewDiagnostics( compilerDiagnostics, GetCompilerDiagnostics( document ) );

						Assert.True( false,
							System.String.Format( "Fix introduced new compiler diagnostics:\r\n{0}\r\n\r\nNew document:\r\n{1}\r\n",
								System.String.Join( "\r\n", newCompilerDiagnostics.Select( d => d.ToString() ) ),
								document.GetSyntaxRootAsync().Result.ToFullString() ) );
					}

					//check if there are analyzer diagnostics left after the code fix
					if ( !analyzerDiagnostics.Any() )
					{
						break;
					}
				}

				//after applying all of the code fixes, compare the resulting string to the inputted one
				string actual = GetStringFromDocument( document );
				Assert.Equal( newSource, actual );
			}

			Diagnostic[] GetAnalyzerDiagnosticsTargetedByCodeFixProvider( params Document[] documentsToAnalyze )
				=> GetSortedDiagnosticsFromDocuments( analyzer, documentsToAnalyze )
				.Where( d => codeFixProvider.FixableDiagnosticIds.Contains( d.Id ) )
				.ToArray();
		}

		private static Project CreateProject( string[] sources )
		{
			string fileNamePrefix = _defaultFilePathPrefix;
			string fileExt = _cSharpDefaultFileExt;

			ProjectId projectId = ProjectId.CreateNewId( debugName: _testProjectName );

			AdhocWorkspace workspace = new AdhocWorkspace();
			workspace.Options = workspace.Options.WithProperFormatting();

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
			return project.WithParseOptions( ( (CSharpParseOptions) project.ParseOptions ).WithLanguageVersion( LanguageVersion.Latest ) );
		}

		private static IEnumerable<Diagnostic> GetCompilerDiagnostics( Document document ) => document.GetSemanticModelAsync().Result.GetDiagnostics();

		private static Document ApplyFix( Document document, CodeAction codeAction )
		{
			ImmutableArray<CodeActionOperation> operations = codeAction.GetOperationsAsync( CancellationToken.None ).Result;
			Solution solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
			return solution.GetDocument( document.Id );
		}

		private static IEnumerable<Diagnostic> GetNewDiagnostics( IEnumerable<Diagnostic> diagnostics, IEnumerable<Diagnostic> newDiagnostics )
		{
			Diagnostic[] oldArray = diagnostics.OrderBy( d => d.Location.SourceSpan.Start ).ToArray();
			Diagnostic[] newArray = newDiagnostics.OrderBy( d => d.Location.SourceSpan.Start ).ToArray();

			int oldIndex = 0;
			int newIndex = 0;

			while ( newIndex < newArray.Length )
			{
				if ( oldIndex < oldArray.Length && oldArray[ oldIndex ].Id == newArray[ newIndex ].Id )
				{
					++oldIndex;
					++newIndex;
				}
				else
				{
					yield return newArray[ newIndex++ ];
				}
			}
		}

		private static string GetStringFromDocument( Document document )
		{
			Document simplifiedDoc = Simplifier.ReduceAsync( document, Simplifier.Annotation ).Result;
			SyntaxNode root = simplifiedDoc.GetSyntaxRootAsync().Result;
			root = Formatter.Format( root, Formatter.Annotation, simplifiedDoc.Project.Solution.Workspace );
			return root.GetText().ToString();
		}

		private static Diagnostic[] GetSortedDiagnosticsFromDocuments( DiagnosticAnalyzer analyzer, Document[] documents )
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
