using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
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

#pragma warning disable HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
	public readonly struct AnalyzerVerifier<TAnalyzer>
		where TAnalyzer : DiagnosticAnalyzer, new()
	{
		public AnalyzerVerifier<TAnalyzer, TCodeFix> UseCodeFix<TCodeFix>()
			where TCodeFix : CodeFixProvider, new()
			=> default;

		public AnalyzerVerifier<TAnalyzer, string, SingleSourceCollectionFactory> Source( string source )
			=> new AnalyzerVerifier<TAnalyzer, string, SingleSourceCollectionFactory>( source );
	}

	public readonly struct AnalyzerVerifier<TAnalyzer, TSource, TSourceCollectionFactory>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TSourceCollectionFactory : struct, ISourceCollectionFactory<TSource>
	{
		private readonly TSource _source;

		public AnalyzerVerifier( TSource source ) => _source = source;

		public void ShouldHaveNoDiagnostics() => Assert.Empty( GetDiagnostics() );

		public void ShouldHaveDiagnostics( Action<Diagnostic[]> asserter )
		{
			Diagnostic[] diagnostics = GetDiagnostics();
			Assert.NotEmpty( diagnostics );
			asserter( diagnostics );
		}

		private Diagnostic[] GetDiagnostics()
		{
			string[] sources = default( TSourceCollectionFactory ).CreateCollection( _source );
			Project project = ProjectUtils.CreateProject( sources );
			Document[] documents = project.Documents.ToArray();
			Diagnostic[] diagnostics = ProjectUtils.GetSortedDiagnosticsFromDocuments( new TAnalyzer(), documents );
			return diagnostics;
		}
	}

	public readonly struct AnalyzerVerifier<TAnalyzer, TCodeFix>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TCodeFix : CodeFixProvider, new()
	{
		private readonly Func<OptionSet, OptionSet> _optionConfigurator;
		private readonly Func<CSharpParseOptions, CSharpParseOptions> _parseOptionsConfigurator;

		public AnalyzerVerifier( Func<OptionSet, OptionSet> optionConfigurator, Func<CSharpParseOptions, CSharpParseOptions> parseOptionsConfigurator )
		{
			_optionConfigurator = optionConfigurator;
			_parseOptionsConfigurator = parseOptionsConfigurator;
		}

		public AnalyzerVerifier<TAnalyzer, TCodeFix> WithOptions( Func<OptionSet, OptionSet> optionConfigurator )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix>( optionConfigurator, _parseOptionsConfigurator );

		public AnalyzerVerifier<TAnalyzer, TCodeFix> WithParseOptions( Func<CSharpParseOptions, CSharpParseOptions> parseOptionsConfigurator )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix>( _optionConfigurator, parseOptionsConfigurator );

		public AnalyzerVerifier<TAnalyzer, TCodeFix, string, SingleSourceCollectionFactory> Source( string source )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix, string, SingleSourceCollectionFactory>( source, _optionConfigurator, _parseOptionsConfigurator );

		public AnalyzerVerifier<TAnalyzer, TCodeFix, string[], MultiSourceCollectionFactory> Sources( params string[] sources )
			=> new AnalyzerVerifier<TAnalyzer, TCodeFix, string[], MultiSourceCollectionFactory>( sources, _optionConfigurator, _parseOptionsConfigurator );
	}

	public readonly struct AnalyzerVerifier<TAnalyzer, TCodeFix, TSource, TSourceCollectionFactory>
		where TAnalyzer : DiagnosticAnalyzer, new()
		where TCodeFix : CodeFixProvider, new()
		where TSourceCollectionFactory : struct, ISourceCollectionFactory<TSource>
	{
		private readonly Func<OptionSet, OptionSet> _optionConfigurator;
		private readonly Func<CSharpParseOptions, CSharpParseOptions> _parseOptionsConfigurator;
		private readonly TSource _sources;

		public AnalyzerVerifier( TSource sources, Func<OptionSet, OptionSet> optionConfigurator, Func<CSharpParseOptions, CSharpParseOptions> parseOptionsConfigurator )
		{
			_sources = sources;
			_optionConfigurator = optionConfigurator;
			_parseOptionsConfigurator = parseOptionsConfigurator;
		}

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

		private void VerifyNoFix( string[] sources )
		{
			Document[] documents = GetDocuments( sources );
			Diagnostic[] analyzerDiagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( new TAnalyzer(), new TCodeFix(), documents );

			if ( analyzerDiagnostics.Length > 0 )
			{
				string assertMessage = String.Join
				(
					Environment.NewLine + Environment.NewLine,
					analyzerDiagnostics.Select( d => GetUnexpectedDiagnosticAssertMessage( d ) )
				);

				Assert.True( false, assertMessage );
			}

			string GetUnexpectedDiagnosticAssertMessage( Diagnostic diagnostic )
				=>
$@"### {diagnostic.Id}: {diagnostic.GetMessage()} @ {diagnostic.Location.ToString()}

{GetSourceWithLocationHighlighted( diagnostic.Location )}
";

			string GetSourceWithLocationHighlighted( Location location )
			{
				const string startMarker = ">>>";
				const string endMarker = "<<<";

				SourceText originalSourceText = location.SourceTree.GetText();

				StringWriter writer = new StringWriter( new StringBuilder( originalSourceText.Length + startMarker.Length + endMarker.Length ) );
				writer.Write( originalSourceText.GetSubText( TextSpan.FromBounds( 0, location.SourceSpan.Start ) ).ToString() );
				writer.Write( startMarker );
				writer.Write( originalSourceText.GetSubText( location.SourceSpan ).ToString() );
				writer.Write( endMarker );
				writer.Write( originalSourceText.GetSubText( location.SourceSpan.End ).ToString() );
				return writer.ToString();
			}
		}

		private void VerifyCSharpFix( string[] sources, string[] fixedSources, int? codeFixIndex )
			=> VerifyFix( new TAnalyzer(), new TCodeFix(), sources, fixedSources, codeFixIndex );

		private void VerifyFix( DiagnosticAnalyzer analyzer, CodeFixProvider codeFixProvider, string[] oldSources, string[] newSources, int? codeFixIndex )
		{
			Solution solution = CreateProject( oldSources ).Solution;
			while ( true )
			{
				Document[] documents = solution.Projects.SelectMany( p => p.Documents ).ToArray();

				Diagnostic[] diagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( analyzer, codeFixProvider, documents );
				Diagnostic firstDiagnostic = diagnostics.FirstOrDefault();

				if ( firstDiagnostic is null )
					break;

				List<CodeAction> actions = new();
				Document document = solution.GetDocument( firstDiagnostic.Location.SourceTree );
				TextSpan span = firstDiagnostic.Location.SourceSpan;
				ImmutableArray<Diagnostic> spanDiagnostics = ImmutableArray.Create( firstDiagnostic );
				CodeFixContext context = new( document, span, spanDiagnostics, ( a, d ) => actions.Add( a ), CancellationToken.None );
				codeFixProvider.RegisterCodeFixesAsync( context ).Wait();

				if ( !actions.Any() )
					break;

				CodeAction action = codeFixIndex is null ? actions[ 0 ] : actions[ (int) codeFixIndex ];

				ImmutableArray<CodeActionOperation> operations = action.GetOperationsAsync( CancellationToken.None ).Result;
				Solution changedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
				if ( changedSolution == solution )
					break;

				solution = changedSolution;
			}

			Document[] fixedSources = solution.Projects.SelectMany( p => p.Documents ).ToArray();

			for ( int i = 0; i < fixedSources.Length; i++ )
			{
				string fixedSource = GetStringFromDocument( fixedSources[ i ] );
				string expectedSource = newSources[ i ];
				Assert.Equal( expectedSource, fixedSource );
			}
		}

		private Project CreateProject( string[] sources )
			=> ProjectUtils.CreateProject( sources, _optionConfigurator, _parseOptionsConfigurator );

		private Document[] GetDocuments( string[] sources )
		{
			Project project = CreateProject( sources );
			Document[] documents = project.Documents.ToArray();
			return documents;
		}

		private static Diagnostic[] GetAnalyzerDiagnosticsTargetedByCodeFixProvider
		(
			DiagnosticAnalyzer analyzer,
			CodeFixProvider codeFixProvider,
			params Document[] documentsToAnalyze
		)
			=> ProjectUtils.GetSortedDiagnosticsFromDocuments( analyzer, documentsToAnalyze )
			.Where( d => codeFixProvider.FixableDiagnosticIds.Contains( d.Id ) )
			.ToArray();

		private static string GetStringFromDocument( Document document )
		{
			Document simplifiedDoc = Simplifier.ReduceAsync( document, Simplifier.Annotation ).Result;
			SyntaxNode root = simplifiedDoc.GetSyntaxRootAsync().Result;
			root = Formatter.Format( root, Formatter.Annotation, simplifiedDoc.Project.Solution.Workspace );
			return root.GetText().ToString();
		}
	}
#pragma warning restore HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
}
