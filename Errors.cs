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

		public static DiagnosticInfo AmbiguousInputInterface(IFieldSymbol field, int matchCount)
		{
			return new DiagnosticInfo(
				id: "MockGen006",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") implements {2} interfaces with \"InputAttribute\". Specify which one to use with [Input(As = typeof(IXxx))].",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString(), matchCount.ToString() });
		}

		public static DiagnosticInfo AmbiguousOutputInterface(IFieldSymbol field, int matchCount)
		{
			return new DiagnosticInfo(
				id: "MockGen007",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") implements {2} interfaces with \"OutputAttribute\". Specify which one to use with [Output(As = typeof(IXxx))].",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString(), matchCount.ToString() });
		}

		public static DiagnosticInfo AsTargetNotImplemented(IFieldSymbol field, ITypeSymbol asTarget)
		{
			return new DiagnosticInfo(
				id: "MockGen008",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\" (type: \"{1}\") does not implement \"{2}\" specified via \"As\".",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, field.Type.ToString(), asTarget.ToString() });
		}

		public static DiagnosticInfo AsTargetMissingAttribute(IFieldSymbol field, ITypeSymbol asTarget, string requiredAttribute)
		{
			return new DiagnosticInfo(
				id: "MockGen009",
				title: "Source Generator Error",
				messageFormat: "Field \"{0}\": \"As\" target \"{1}\" does not have \"{2}\".",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(field),
				messageArgs: new[] { field.Name, asTarget.ToString(), requiredAttribute });
		}

		public static DiagnosticInfo AsOnNonField(ISymbol symbol)
		{
			return new DiagnosticInfo(
				id: "MockGen010",
				title: "Source Generator Error",
				messageFormat: "\"As\" parameter is only valid on fields. (symbol: \"{0}\")",
				severity: DiagnosticSeverity.Error,
				location: LocationInfo.From(symbol),
				messageArgs: new[] { symbol.ToString() });
		}
	}
}
