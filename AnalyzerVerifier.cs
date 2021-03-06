﻿using System;
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
			Document[] documents = GetDocuments( oldSources );
			Diagnostic[] analyzerDiagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( analyzer, codeFixProvider, documents );
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
					analyzerDiagnostics = GetAnalyzerDiagnosticsTargetedByCodeFixProvider( analyzer, codeFixProvider, document );

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
		}

		private Document[] GetDocuments( string[] sources )
		{
			Project project = ProjectUtils.CreateProject( sources, _optionConfigurator, _parseOptionsConfigurator );
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
	}
#pragma warning restore HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
}
