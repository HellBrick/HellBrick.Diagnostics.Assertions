using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace HellBrick.Diagnostics.Assertions
{
	internal static class ProjectUtils
	{
		private static readonly MetadataReference _corlibReference = MetadataReference.CreateFromFile( typeof( object ).Assembly.Location );
		private static readonly MetadataReference _systemCoreReference = MetadataReference.CreateFromFile( typeof( Enumerable ).Assembly.Location );
		private static readonly MetadataReference _cSharpSymbolsReference = MetadataReference.CreateFromFile( typeof( CSharpCompilation ).Assembly.Location );
		private static readonly MetadataReference _codeAnalysisReference = MetadataReference.CreateFromFile( typeof( Compilation ).Assembly.Location );

		private static string _defaultFilePathPrefix = "Test";
		private static string _cSharpDefaultFileExt = "cs";
		private static string _testProjectName = "TestProject";
		private static string _cSharpDefaultFilePath = _defaultFilePathPrefix + 0 + "." + _cSharpDefaultFileExt;

		public static Project CreateProject( string[] sources )
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
	}
}
