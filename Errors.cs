using Microsoft.CodeAnalysis;
using MockGenerator;
using System;

namespace MockGenereator
{
	internal static class Errors
	{
		public static DiagnosticInfo Unexpected(Exception exception)
		{
			return new DiagnosticInfo(
				id: "MockGen001",
				title: "Source Generator Unexpected Error",
				messageFormat: "An error occurred: {0}",
				severity: DiagnosticSeverity.Error,
				location: null,
				messageArgs: new[] { exception.Message });
		}

		public static DiagnosticInfo IsNotInput(IFieldSymbol field)
		{
			return new DiagnosticInfo(
				id: "MockGen002",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"InputAttribute\".",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString() });
		}

		public static DiagnosticInfo IsNotOutput(IFieldSymbol field)
		{
			return new DiagnosticInfo(
				id: "MockGen003",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"OutputAttribute\".",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString() });
		}

		public static DiagnosticInfo IsNotInputAndOutput(IFieldSymbol field)
		{
			return new DiagnosticInfo(
				id: "MockGen004",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"InputAttribute\" and \"OutputAttribute\".",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString() });
		}

		public static DiagnosticInfo InputOnSetter(IMethodSymbol setter)
		{
			return new DiagnosticInfo(
				id: "MockGen005",
				title: "Source Generator Error",
				messageFormat: "\"InputAttribute\" cannot be applied to a property setter (setter is implicitly Output).",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(setter),
				messageArgs: Array.Empty<string>());
		}
	}
}
