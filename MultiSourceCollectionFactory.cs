namespace HellBrick.Diagnostics.Assertions
{
#pragma warning disable HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
	public readonly struct MultiSourceCollectionFactory : ISourceCollectionFactory<string[]>
	{
		public string[] CreateCollection( string[] sources ) => sources;
	}
#pragma warning restore HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
}
