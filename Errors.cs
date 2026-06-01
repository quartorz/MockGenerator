using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MockGenereator
{
	internal static class Errors
	{
		public static void Unexpected(SourceProductionContext context, Exception exception)
		{
			var diagnostic = Diagnostic.Create(
				new DiagnosticDescriptor(
					id: "MockGen001",
					title: "Source Generator Unexpected Error",
					messageFormat: "An error occurred: {0}",
					category: "SourceGenerator",
					DiagnosticSeverity.Error,
					isEnabledByDefault: true),
				Location.None,
				exception.Message
			);
			context.ReportDiagnostic(diagnostic);
		}

		public static void IsNotInput(SourceProductionContext context, IFieldSymbol field)
		{
			var diagnostic = Diagnostic.Create(
				new DiagnosticDescriptor(
					id: "MockGen002",
					title: "Source Generator Error",
					messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"InputAttribute\".",
					category: "SourceGenerator",
					DiagnosticSeverity.Error,
					isEnabledByDefault: true),
				field.Locations.FirstOrDefault(),
				field.Name,
				field.Type
			);
			context.ReportDiagnostic(diagnostic);
		}

		public static void IsNotOutput(SourceProductionContext context, IFieldSymbol field)
		{
			var diagnostic = Diagnostic.Create(
				new DiagnosticDescriptor(
					id: "MockGen003",
					title: "Source Generator Error",
					messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"OutputAttribute\".",
					category: "SourceGenerator",
					DiagnosticSeverity.Error,
					isEnabledByDefault: true),
				field.Locations.FirstOrDefault() ?? Location.None,
				field.Name,
				field.Type
			);
			context.ReportDiagnostic(diagnostic);
		}

		public static void IsNotInputAndOutput(SourceProductionContext context, IFieldSymbol field)
		{
			var diagnostic = Diagnostic.Create(
				new DiagnosticDescriptor(
					id: "MockGen004",
					title: "Source Generator Error",
					messageFormat: "Field \"{0}\" (type: \"{1}\") must implement an interface with the \"InputAttribute\" and \"OutputAttribute\".",
					category: "SourceGenerator",
					DiagnosticSeverity.Error,
					isEnabledByDefault: true),
				field.Locations.FirstOrDefault(),
				field.Name,
				field.Type
			);
			context.ReportDiagnostic(diagnostic);
		}
	}
}
