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

			//var source = context.SyntaxProvider.CreateSyntaxProvider(
			//	static (node, token) => true,
			//	static (ctx, token) =>
			//	{
			//		ctx.Node.SyntaxTree.
			//		return 0;				
			//	});

			context.RegisterSourceOutput(
				context.SyntaxProvider.ForAttributeWithMetadataName(
					"MockGenerator.GenerateViewInterfacesAttribute",
					static (node, token) => true,
					static (ctx, token) => ctx),
				GenerateViewInterfaces);
			//context.RegisterSourceOutput(
			//	context.SyntaxProvider.ForAttributeWithMetadataName(
			//		"MockGenerator.GenerateViewInterfacesAttribute",
			//		static (node, token) => true,
			//		Transform),
			//	(_, _) => { });

			context.RegisterSourceOutput(
				context.SyntaxProvider.ForAttributeWithMetadataName(
					"MockGenerator.GenerateMockViewAttribute",
					static (node, token) => true,
					static (ctx, token) => ctx),
				GenerateMockView);
		}

		static Source Transform(GeneratorAttributeSyntaxContext source, CancellationToken token)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;
			return new Source(
				typeSymbol.ContainingNamespace.IsGlobalNamespace ? null : typeSymbol.ContainingNamespace.ToString(), typeSymbol.Name, null, [new("", "b")], [], [], [] ,[]);
		}

		static void RunGenerator(SourceProductionContext context, GeneratorAttributeSyntaxContext source,
			string hintName, Action<SourceProductionContext, GeneratorAttributeSyntaxContext, StringBuilder> generator)
		{
			using var _ = StringBuilderHolder.Get(out var sb);
			try
			{
				generator(context, source, sb);
			}
			catch (Exception e)
			{
				Errors.Unexpected(context, e);
				sb.AppendFormat("\n#error {0}", e.Message);
			}
			context.AddSource(hintName, sb.ToString());
		}

		static void GenerateViewInterfaces(SourceProductionContext context, GeneratorAttributeSyntaxContext source)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			// Diagnostics: [Input] on setter is invalid (setter is implicitly Output).
			foreach (var member in typeSymbol.GetMembers())
			{
				if (member is IPropertySymbol prop && prop.SetMethod != null
					&& prop.SetMethod.HasAttribute("MockGenerator", "InputAttribute"))
				{
					Errors.InputOnSetter(context, prop.SetMethod);
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


			RunGenerator(context, source, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Input.cs", (context, syntax, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
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
							Errors.IsNotInput(context, field);
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

			RunGenerator(context, source, $"{Namespace(typeSymbol)}.I{typeSymbol.Name}Output.cs", (context, source, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
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
							Errors.IsNotOutput(context, field);
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

			RunGenerator(context, source, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.I{typeSymbol.Name}.cs", (context, source, sb) =>
			{
				sb.Append($$"""
				namespace {{Namespace(typeSymbol)}}
				{
					public interface I{{typeSymbol.Name}}{{typeSymbol.TypeParameters.GenericsParams()}} :
						I{{typeSymbol.Name}}Input{{typeSymbol.TypeParameters.GenericsParams()}},
						I{{typeSymbol.Name}}Output{{typeSymbol.TypeParameters.GenericsParams()}}{{typeSymbol.TypeParameters.GenericsConstraints()}}
					{
					}
				}
				""");
			});

			RunGenerator(context, source, $"{(typeSymbol.ContainingNamespace.IsGlobalNamespace ? "global" : typeSymbol.ContainingNamespace)}.{typeSymbol.Name}.cs", (context, source, sb) =>
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
						Errors.IsNotInputAndOutput(context, field);
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
		}

		static void GenerateMockView(SourceProductionContext context, GeneratorAttributeSyntaxContext source)
		{
			var typeSymbol = (INamedTypeSymbol)source.TargetSymbol;
			var typeNode = (TypeDeclarationSyntax)source.TargetNode;

			RunGenerator(context, source, $"{Namespace(typeSymbol)}.Mock{typeSymbol.Name}.cs", (context, source, sb) =>
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
						if (isInput && isOutput)
						{
							var inputName = field.Type.ResolveViewInterfaceName(input: true, output: false);
							var outputName = field.Type.ResolveViewInterfaceName(input: false, output: true);
							if (inputName == null || outputName == null)
							{
								Errors.IsNotInputAndOutput(context, field);
							}
							else
							{
								sb.Append($"\n		public {inputName} {prop}Input {{ get; set; }}");
								sb.Append($"\n		{inputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Input{generics}.{prop} => {prop}Input;");
								sb.Append($"\n		public {outputName} {prop}Output {{ get; set; }}");
								sb.Append($"\n		{outputName} {Namespace(typeSymbol)}.I{typeSymbol.Name}Output{generics}.{prop} => {prop}Output;");
							}
						}
						else if (isInput)
						{
							var typeName = field.Type.ResolveViewInterfaceName(input: true, output: false);
							if (typeName == null)
							{
								Errors.IsNotInput(context, field);
							}
							sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", typeName ?? field.Type.QualifiedName(), prop);
						}
						else if (isOutput)
						{
							var typeName = field.Type.ResolveViewInterfaceName(input: false, output: true);
							if (typeName == null)
							{
								Errors.IsNotOutput(context, field);
							}
							sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", typeName ?? field.Type.QualifiedName(), prop);
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
						sb.AppendFormat("\n		public {0} {1} {{ get; set; }}", rawType, prop);
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
