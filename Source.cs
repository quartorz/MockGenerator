using System;
using System.Collections.Generic;
using System.Text;

#nullable enable

namespace System.Runtime.CompilerServices
{
	internal class IsExternalInit { }
}

namespace MockGenerator
{
	internal sealed record class Generics(string GenericsParams, string Constraints);
	internal sealed record class Field(string Type, string Name);
	internal sealed record class Property(string Type, string Name);
	internal sealed record class Method(string Name, string ReturnType, string Params, Generics? Generics);

	internal sealed record class Source(
		string? Namespace, string Name, Generics? Generics,
		Field[] InFields, Property[] InProperties,
		Field[] OutFields, Property[] OutProperties, Method[] OutMethods) : IEquatable<Source>
	{
#pragma warning disable CS8851
		public bool Equals(Source source)
#pragma warning restore CS8851
		{
			return Namespace == source.Namespace && Name == source.Name &&
				InFields.AsSpan().SequenceEqual(source.InFields) &&
				InProperties.AsSpan().SequenceEqual(source.InProperties) && 
				OutFields.AsSpan().SequenceEqual(source.OutFields) &&
				OutProperties.AsSpan().SequenceEqual(source.OutProperties) && 
				OutMethods.AsSpan().SequenceEqual(source.OutMethods);
		}
	}
}
