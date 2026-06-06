using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MockGenerator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

namespace MockGenereator
{
	[Generator]
	public class MockGenerator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
		{
			context.RegisterPostInitializationOutput(ctx =>
			{
				ctx.AddSource("GenerateViewInterfacesAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateViewInterfacesAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("InputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class InputAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("OutputAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Event, AllowMultiple = false, Inherited = false)]
						internal sealed class OutputAttribute : Attribute
						{
						}
					}
					""");
				ctx.AddSource("GenerateMockViewAttribute.cs", """
					using System;

					namespace MockGenerator
					{
						[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
						internal sealed class GenerateMockViewAttribute : Attribute
						{
						}
					}
					""");
			});

			var viewInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateViewInterfacesAttribute",
				static (node, token) => true,
				TransformViewInterfaces).WithTrackingName("MockGenereator.GenerateViewInterfaces");

			context.RegisterSourceOutput(viewInterfaces, static (spc, result) => Replay(spc, result));

			var mockViews = context.SyntaxProvider.ForAttributeWithMetadataName(
				"MockGenerator.GenerateMockViewAttribute",
				static (node, token) => true,
				TransformMockView).WithTrackingName("MockGenereator.GenerateMockView");

			context.RegisterSourceOutput(mockViews, static (spc, result) => Replay(spc, result));
		}

		static void Replay(SourceProductionContext context, GenerationResult result)
		{
			foreach (var diag in result.Diagnostics)
			{
				context.ReportDiagnostic(diag.ToDiagnostic());
			}
			foreach (var file in result.Files)
			{
				context.AddSource(file.HintName, file.Content);
			}
		}

		static void Emit(List<SourceFile> files, List<DiagnosticInfo> diagnostics, string hintName, Action<StringBuilder> writer)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			try
			{
				writer(sb);
			}
			catch (Exception e)
			{
				diagnostics.Add(Errors.Unexpected(e));
				sb.AppendFormat("\n#error {0}", e.Message);
			}
			files.Add(new SourceFile(hintName, sb.ToString()));
		}

		static GenerationResult TransformViewInterfaces(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var files = new List<SourceFile>();
			var diagnostics = new List<DiagnosticInfo>();

			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			// Diagnostics: [Input] on setter is invalid (setter is implicitly Output).
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IPropertySymbol prop && prop.SetMethod != null
					&& prop.SetMethod.HasAttribute("MockGenerator", "InputAttribute"))
				{
					diagnostics.Add(Errors.InputOnSetter(prop.SetMethod));
				}
			}

			// Field bucketing (only fields go through partial class explicit-impl path).
			var @in = new List<IFieldSymbol>();
			var @out = new List<IFieldSymbol>();
			var inout = new List<IFieldSymbol>();
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IFieldSymbol field)
				{
					var fi = field.HasAttribute("MockGenerator", "InputAttribute");
					var fo = field.HasAttribute("MockGenerator", "OutputAttribute");
					if (fi && fo) inout.Add(field);
					else if (fi) @in.Add(field);
					else if (fo) @out.Add(field);
				}
			}

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Input.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[MockGenerator.Input]
					public interface I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute("MockGenerator", "InputAttribute")) continue;
						var ifield = field.Type.ResolveViewInterfaceName(input: true, output: false);
						if (ifield != null)
						{
							sb.Append($"\n		{ifield} {field.ToPropertyName()} {{ get; }}");
						}
						else
						{
							diagnostics.Add(Errors.IsNotInput(field));
						}
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.HasInputGetter()) continue;
						var iprop = property.Type.ResolveViewInterfaceName(input: true, output: false);
						sb.Append($"\n		{iprop ?? property.Type.QualifiedName()} {property.Name} {{ get; }}");
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "InputAttribute")) continue;
						sb.Append($"\n		{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute("MockGenerator", "InputAttribute")) continue;
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Output.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					[MockGenerator.Output]
					public interface I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						if (!field.HasAttribute("MockGenerator", "OutputAttribute")) continue;
						var ifield = field.Type.ResolveViewInterfaceName(input: false, output: true);
						if (ifield != null)
						{
							sb.Append($"\n		{ifield} {field.ToPropertyName()} {{ get; }}");
						}
						else
						{
							diagnostics.Add(Errors.IsNotOutput(field));
						}
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "OutputAttribute")) continue;
						sb.Append($"\n		{method.ReturnTypeName()} {method.Name}{method.MethodParams()};");
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.IsTracked())
						{
							continue;
						}
						if (property.HasOutputGetter())
						{
							// [Output] getter: composition pull. Setter is suppressed to avoid same-name
							// duplicate declaration in the same interface.
							var iprop = property.Type.ResolveViewInterfaceName(input: false, output: true);
							sb.Append($"\n		{iprop ?? property.Type.QualifiedName()} {property.Name} {{ get; }}");
						}
						else if (property.SetMethod != null)
						{
							sb.Append($"\n		{property.Type.QualifiedName()} {property.Name} {{ set; }}");
						}
					}
					else if (member is IEventSymbol @event)
					{
						if (!@event.HasAttribute("MockGenerator", "OutputAttribute"))
						{
							continue;
						}
						sb.Append($"\n		event {@event.Type.QualifiedName()} {@event.Name};");
					}
				}

				sb.Append("""

					}
				}
				""");
			});

			Emit(files, diagnostics, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.I{typeSymbol.Name}.cs", sb =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					/// <summary>
					/// <see cref="{{typeSymbol.ToCref()}}"/>
					/// </summary>
					public interface I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}},
						I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
					}
				}
				""");
			});

			Emit(files, diagnostics, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.{typeSymbol.Name}.cs", sb =>
			{
				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append($$"""
					namespace {{typeSymbol.ContainingNamespace}}
					{
					""");
				}

				sb.Append($$"""

					public partial class {{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						{{Namespace(typeSymbol)}}.I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
				""");

				foreach (var field in @in)
				{
					var ifield = field.Type.ResolveViewInterfaceName(input: true, output: false);
					if (ifield != null)
					{
						sb.Append($"\n		public {ifield} {field.ToPropertyName()} => {field.Name};");
					}
				}

				foreach (var field in @out)
				{
					var ifield = field.Type.ResolveViewInterfaceName(input: false, output: true);
					if (ifield != null)
					{
						sb.Append($"\n		public {ifield} {field.ToPropertyName()} => {field.Name};");
					}
				}

				foreach (var field in inout)
				{
					var inputName = field.Type.ResolveViewInterfaceName(input: true, output: false);
					var outputName = field.Type.ResolveViewInterfaceName(input: false, output: true);
					if (inputName != null && outputName != null)
					{
						var prop = field.ToPropertyName();
						var generics = typeSymbol.TypeParameters.GenericsParams();
						sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {field.Name};");
						sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {field.Name};");
					}
					else
					{
						diagnostics.Add(Errors.IsNotInputAndOutput(field));
					}
				}

				sb.Append("""

					}
				""");

				if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
				{
					sb.Append("""

					}
					""");
				}
			});

			return new GenerationResult(files.ToArray(), diagnostics.ToArray());
		}

		static GenerationResult TransformMockView(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var files = new List<SourceFile>();
			var diagnostics = new List<DiagnosticInfo>();

			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			Emit(files, diagnostics, $"{Namespace(typeSymbol)}.Mock{typeSymbol.Name}.cs", sb =>
			{
				sb.Append($$"""
					namespace {{Namespace(typeSymbol)}}
					{
						/// <summary>
						/// <see cref="{{typeSymbol.ToCref()}}"/>
						/// </summary>
						public partial class Mock{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} : I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
						{
					""");

				foreach (var member in typeSymbol.GetMembers())
				{
					if (member is IFieldSymbol field)
					{
						var isInput = field.HasAttribute("MockGenerator", "InputAttribute");
						var isOutput = field.HasAttribute("MockGenerator", "OutputAttribute");
						if (!isInput && !isOutput)
						{
							continue;
						}
						var prop = field.ToPropertyName();
						var generics = typeSymbol.TypeParameters.GenericsParams();
						var inputName = isInput ? field.Type.ResolveViewInterfaceName(input: true, output: false) : null;
						var outputName = isOutput ? field.Type.ResolveViewInterfaceName(input: false, output: true) : null;
						if (isInput && isOutput && (inputName == null || outputName == null))
						{
							diagnostics.Add(Errors.IsNotInputAndOutput(field));
							continue;
						}
						if (isInput && !isOutput && inputName == null)
						{
							diagnostics.Add(Errors.IsNotInput(field));
							continue;
						}
						if (isOutput && !isInput && outputName == null)
						{
							diagnostics.Add(Errors.IsNotOutput(field));
							continue;
						}
						var resolvedMock = field.Type.ResolveMockTypeName();
						var mockType = resolvedMock ?? field.Type.QualifiedName();
						var initializer = resolvedMock != null ? $" = new {resolvedMock}();" : "";
						sb.Append($"\n		public {mockType} {prop} {{ get; set; }}{initializer}");
						if (isInput)
						{
							sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {prop};");
						}
						if (isOutput)
						{
							sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {prop};");
						}
					}
					else if (member is IPropertySymbol property)
					{
						if (!property.IsTracked())
						{
							continue;
						}
						var prop = property.Name;
						var generics = typeSymbol.TypeParameters.GenericsParams();
						var rawType = property.Type.QualifiedName();
						var hasOutputSetter = property.SetMethod != null
							&& property.SetMethod.HasAttribute("MockGenerator", "OutputAttribute");
						if (hasOutputSetter)
						{
							var pascal = char.ToUpper(prop[0]) + prop.Substring(1);
							var camel = char.ToLower(prop[0]) + prop.Substring(1);
							var backing = "_" + camel + "Backing";
							var onSet = "On" + pascal + "Set";
							sb.Append($"\n		public System.Action<{rawType}> {onSet} {{ get; set; }}");
							sb.Append($"\n		private {rawType} {backing};");
							sb.Append($"\n		public {rawType} {prop} {{ get => {backing}; set {{ {backing} = value; {onSet}?.Invoke(value); }} }}");
						}
						else
						{
							sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", rawType, prop);
						}
						if (property.HasInputGetter())
						{
							var inputName = property.Type.ResolveViewInterfaceName(input: true, output: false);
							if (inputName != null && inputName != rawType)
							{
								sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {prop};");
							}
						}
						if (property.HasOutputGetter())
						{
							var outputName = property.Type.ResolveViewInterfaceName(input: false, output: true);
							if (outputName != null && outputName != rawType)
							{
								sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {prop};");
							}
						}
					}
					else if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
					{
						if (!method.HasAttribute("MockGenerator", "InputAttribute")
							&& !method.HasAttribute("MockGenerator", "OutputAttribute"))
						{
							continue;
						}
						sb.EmitMockMethodMember("\t\t", method);
					}
					else if (member is IEventSymbol @event)
					{
						var isInput = @event.HasAttribute("MockGenerator", "InputAttribute");
						var isOutput = @event.HasAttribute("MockGenerator", "OutputAttribute");
						if (!isInput && !isOutput)
						{
							continue;
						}
						sb.EmitMockEventMember("\t\t", @event);
					}
				}

				sb.Append("""

						}
					}
					""");
			});

			return new GenerationResult(files.ToArray(), diagnostics.ToArray());
		}

		static string Namespace(ISymbol symbol)
		{
			if (symbol.ContainingNamespace.IsGlobalNamespace)
			{
				return "MockView";
			}
			else
			{
				return $"MockView.{symbol.ContainingNamespace}";
			}
		}

	}
}
