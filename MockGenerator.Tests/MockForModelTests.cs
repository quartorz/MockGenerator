using Xunit;

namespace MockGenerator.Tests;

/// <summary>Golden tests for the mock emitted from <c>[GenerateMockFor]</c>.</summary>
public class MockForModelTests
{
	static GeneratorRunner.RunResult Run(string source) =>
		GeneratorRunner.Run(source, new global::MockGenerator.GenerateMockForGenerator());

	/// <summary>A single interface produces unprefixed members: property, event plus raiser, and method.</summary>
	[Fact]
	public void SingleInterfaceProducesUnprefixedMembers()
	{
		var r = Run(/* lang=c# */ """
			using MockGenerator;

			namespace Sample
			{
				public interface ISession
				{
					string Name { get; set; }
					event System.Action OnClosed;
					int Count(string key);
				}

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Golden.Equal("MockFor_SingleInterface", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}

	/// <summary>
	/// When base interfaces declare the same event name with different delegate types, each one gets a
	/// prefixed slot whose raiser keeps the prefix in front — <c>IUpper_RaiseOnChanged</c>.
	/// </summary>
	[Fact]
	public void ConflictingEventTypesGetPrefixedSlotAndPrefixedRaiser()
	{
		var r = Run(/* lang=c# */ """
			using MockGenerator;

			namespace Sample
			{
				public interface IUpper
				{
					event System.Action<int> OnChanged;
				}

				public interface ILower
				{
					event System.Action<string> OnChanged;
				}

				public interface ISession : IUpper, ILower { }

				[GenerateMockFor(typeof(ISession))]
				public partial class MockSession { }
			}
			""");

		Assert.Empty(r.Diagnostics);
		Golden.Equal("MockFor_ConflictingEventTypes", r.Source("Sample.MockSession.GenerateMockFor.cs"));
	}
}
