using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;
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

	internal sealed record class SourceFile(string HintName, string Content);

	internal sealed record class LocationInfo(string FilePath, TextSpan SourceSpan, LinePositionSpan LineSpan)
	{
		public Location ToLocation() => Location.Create(FilePath, SourceSpan, LineSpan);

		public static LocationInfo? From(Location? location)
		{
			if (location == null || location.SourceTree == null) return null;
			return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
		}

		public static LocationInfo? From(ISymbol symbol) => From(symbol.Locations.FirstOrDefault());
	}

	internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
	{
		public string Id { get; }
		public string Title { get; }
		public string MessageFormat { get; }
		public DiagnosticSeverity Severity { get; }
		public LocationInfo? Location { get; }
		public string[] MessageArgs { get; }

		public DiagnosticInfo(string id, string title, string messageFormat, DiagnosticSeverity severity,
			LocationInfo? location, string[] messageArgs)
		{
			Id = id;
			Title = title;
			MessageFormat = messageFormat;
			Severity = severity;
			Location = location;
			MessageArgs = messageArgs;
		}

		public Diagnostic ToDiagnostic()
		{
			var descriptor = new DiagnosticDescriptor(Id, Title, MessageFormat, "SourceGenerator", Severity, isEnabledByDefault: true);
			return Diagnostic.Create(descriptor, Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None, MessageArgs);
		}

		public bool Equals(DiagnosticInfo? other)
		{
			if (other == null) return false;
			return Id == other.Id && Title == other.Title && MessageFormat == other.MessageFormat &&
				Severity == other.Severity && Equals(Location, other.Location) &&
				MessageArgs.AsSpan().SequenceEqual(other.MessageArgs);
		}

		public override bool Equals(object? obj) => obj is DiagnosticInfo d && Equals(d);

		public override int GetHashCode()
		{
			var hash = Id.GetHashCode() ^ MessageFormat.GetHashCode();
			foreach (var arg in MessageArgs) hash ^= arg?.GetHashCode() ?? 0;
			return hash;
		}
	}

	internal sealed class GenerationResult : IEquatable<GenerationResult>
	{
		public SourceFile[] Files { get; }
		public DiagnosticInfo[] Diagnostics { get; }

		public GenerationResult(SourceFile[] files, DiagnosticInfo[] diagnostics)
		{
			Files = files;
			Diagnostics = diagnostics;
		}

		public bool Equals(GenerationResult? other)
		{
			if (other == null) return false;
			return Files.AsSpan().SequenceEqual(other.Files) &&
				Diagnostics.AsSpan().SequenceEqual(other.Diagnostics);
		}

		public override bool Equals(object? obj) => obj is GenerationResult r && Equals(r);

		public override int GetHashCode()
		{
			var hash = 0;
			foreach (var f in Files) hash ^= f.GetHashCode();
			foreach (var d in Diagnostics) hash ^= d.GetHashCode();
			return hash;
		}
	}
}
