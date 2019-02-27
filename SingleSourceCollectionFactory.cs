namespace HellBrick.Diagnostics.Assertions
{
#pragma warning disable HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
	public readonly struct SingleSourceCollectionFactory : ISourceCollectionFactory<string>
	{
		public string[] CreateCollection( string sources ) => new[] { sources };
	}
#pragma warning restore HBStructEquatabilityMethodsMissing // Structs should provide equatability methods 
}
