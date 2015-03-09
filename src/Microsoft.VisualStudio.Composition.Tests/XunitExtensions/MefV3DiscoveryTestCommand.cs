﻿namespace Microsoft.VisualStudio.Composition.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using DiffPlex;
    using DiffPlex.DiffBuilder;
    using DiffPlex.DiffBuilder.Model;
    using Xunit;
    using Xunit.Abstractions;
    using Xunit.Sdk;

    public class MefV3DiscoveryTestCommand : XunitTestCaseRunner
    {
        private readonly CompositionEngines compositionVersions;
        private readonly bool expectInvalidConfiguration;
        private readonly Type[] parts;
        private readonly IReadOnlyList<string> assemblyNames;

        public MefV3DiscoveryTestCommand(IXunitTestCase testCase, string displayName, string skipReason, object[] constructorArguments, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, CompositionEngines compositionEngines, Type[] parts, IReadOnlyList<string> assemblyNames, bool expectInvalidConfiguration)
            : base(testCase, displayName, skipReason, constructorArguments, null, messageBus, aggregator, cancellationTokenSource)
        {
            Requires.NotNull(testCase, nameof(testCase));
            Requires.NotNull(parts, nameof(parts));
            Requires.NotNull(assemblyNames, nameof(assemblyNames));

            this.compositionVersions = compositionEngines;
            this.assemblyNames = assemblyNames;
            this.parts = parts;
            this.expectInvalidConfiguration = expectInvalidConfiguration;
        }

        public IReadOnlyList<CompositionConfiguration> ResultingConfigurations { get; set; }

        public bool Passed { get; private set; }

        /// <summary>
        /// Gets the object which measures execution time.
        /// </summary>
        protected ExecutionTimer Timer { get; } = new ExecutionTimer();

        protected override async Task<RunSummary> RunTestAsync()
        {
            var output = new TestOutputHelper();
            output.Initialize(this.MessageBus, new XunitTest(this.TestCase, this.DisplayName));
            await this.Aggregator.RunAsync(() => this.Timer.AggregateAsync(
                async delegate
                {
                    var v3DiscoveryModules = this.GetV3DiscoveryModules();

                    var resultingCatalogs = new List<ComposableCatalog>(v3DiscoveryModules.Count);

                    var assemblies = this.assemblyNames.Select(Assembly.Load).ToList();
                    foreach (var discoveryModule in v3DiscoveryModules)
                    {
                        var partsFromTypes = await discoveryModule.CreatePartsAsync(this.parts);
                        var partsFromAssemblies = await discoveryModule.CreatePartsAsync(assemblies);
                        var catalog = ComposableCatalog.Create()
                            .WithParts(partsFromTypes)
                            .WithParts(partsFromAssemblies);
                        resultingCatalogs.Add(catalog);
                    }

                    string[] catalogStringRepresentations = resultingCatalogs.Select(catalog =>
                        {
                            var writer = new StringWriter();
                            catalog.ToString(writer);
                            return writer.ToString();
                        }).ToArray();

                    bool anyStringRepresentationDifferences = false;
                    for (int i = 1; i < resultingCatalogs.Count; i++)
                    {
                        anyStringRepresentationDifferences = PrintDiff(
                            v3DiscoveryModules[0].GetType().Name,
                            v3DiscoveryModules[i].GetType().Name,
                            catalogStringRepresentations[0],
                            catalogStringRepresentations[i],
                            output);
                    }

                    // Verify that the catalogs are identical.
                    // The string compare above should have taken care of this (in a more descriptive way),
                    // but we do this to double-check.
                    var uniqueCatalogs = resultingCatalogs.Distinct().ToArray();

                    // Fail the test if ComposableCatalog.Equals returns a different result from string comparison.
                    Assert.Equal(anyStringRepresentationDifferences, uniqueCatalogs.Length > 1);

                    if (uniqueCatalogs.Length == 1)
                    {
                        ////output.WriteLine(catalogStringRepresentations[0]);
                    }

                    // For each distinct catalog, create one configuration and verify it meets expectations.
                    var configurations = new List<CompositionConfiguration>(uniqueCatalogs.Length);
                    foreach (var uniqueCatalog in uniqueCatalogs)
                    {
                        var catalogWithSupport = uniqueCatalog
                            .WithCompositionService()
                            .WithDesktopSupport();

                        // Round-trip the catalog through serialization to verify that as well.
                        RoundtripCatalogSerialization(catalogWithSupport, output);

                        var configuration = CompositionConfiguration.Create(catalogWithSupport);

                        if (!this.compositionVersions.HasFlag(CompositionEngines.V3AllowConfigurationWithErrors))
                        {
                            Assert.Equal(this.expectInvalidConfiguration, !configuration.CompositionErrors.IsEmpty || !catalogWithSupport.DiscoveredParts.DiscoveryErrors.IsEmpty);
                        }

                        // Save the configuration in a property so that the engine test that follows can reuse the work we've done.
                        configurations.Add(configuration);
                    }

                    this.ResultingConfigurations = configurations;
                }));

            var test = new XunitTest(this.TestCase, this.DisplayName);
            var runSummary = new RunSummary { Total = 1, Time = this.Timer.Total };
            IMessageSinkMessage testResultMessage;
            if (this.Aggregator.HasExceptions)
            {
                testResultMessage = new TestFailed(test, this.Timer.Total, output.Output, this.Aggregator.ToException());
                runSummary.Failed++;
            }
            else
            {
                testResultMessage = new TestPassed(test, this.Timer.Total, output.Output);
                this.Passed = true;
            }

            if (!this.MessageBus.QueueMessage(testResultMessage))
            {
                CancellationTokenSource.Cancel();
            }

            this.Aggregator.Clear();
            return runSummary;
        }

        private static bool PrintDiff(string beforeDescription, string afterDescription, string before, string after, ITestOutputHelper output)
        {
            Requires.NotNull(output, nameof(output));

            var d = new Differ();
            var inlineBuilder = new InlineDiffBuilder(d);
            var result = inlineBuilder.BuildDiffModel(before, after);
            if (result.Lines.Any(l => l.Type != ChangeType.Unchanged))
            {
                output.WriteLine("Catalog {0} vs. {1}", beforeDescription, afterDescription);
                foreach (var line in result.Lines)
                {
                    if (line.Type == ChangeType.Inserted)
                    {
                        Console.Write("+ ");
                    }
                    else if (line.Type == ChangeType.Deleted)
                    {
                        Console.Write("- ");
                    }
                    else
                    {
                        Console.Write("  ");
                    }

                    output.WriteLine(line.Text);
                }

                return true;
                ////Assert.False(anyStringRepresentationDifferences, "Catalogs not equivalent");
            }

            return false;
        }

        private static void RoundtripCatalogSerialization(ComposableCatalog catalog, ITestOutputHelper output)
        {
            Requires.NotNull(catalog, "catalog");
            Requires.NotNull(output, nameof(output));

            var catalogSerialization = new CachedCatalog();
            var ms = new MemoryStream();
            catalogSerialization.SaveAsync(catalog, ms).Wait();
            ms.Position = 0;
            var deserializedCatalog = catalogSerialization.LoadAsync(ms).Result;

            var before = new StringWriter();
            catalog.ToString(before);
            var after = new StringWriter();
            deserializedCatalog.ToString(after);

            PrintDiff("BeforeSerialization", "AfterSerialization", before.ToString(), after.ToString(), output);

            Assert.True(catalog.Equals(deserializedCatalog));
        }

        private IReadOnlyList<PartDiscovery> GetV3DiscoveryModules()
        {
            var titleAppends = new List<string>();

            var discovery = new List<PartDiscovery>();
            if (this.compositionVersions.HasFlag(CompositionEngines.V3EmulatingV1))
            {
                discovery.Add(new AttributedPartDiscoveryV1());
                titleAppends.Add("V1");
            }

            if (this.compositionVersions.HasFlag(CompositionEngines.V3EmulatingV2))
            {
                discovery.Add(new AttributedPartDiscovery { IsNonPublicSupported = compositionVersions.HasFlag(CompositionEngines.V3EmulatingV2WithNonPublic) });
                titleAppends.Add("V2");
            }

            if (this.compositionVersions.HasFlag(CompositionEngines.V3EmulatingV1AndV2AtOnce))
            {
                discovery.Add(PartDiscovery.Combine(
                    new AttributedPartDiscoveryV1(),
                    new AttributedPartDiscovery { IsNonPublicSupported = compositionVersions.HasFlag(CompositionEngines.V3EmulatingV2WithNonPublic) }));
                titleAppends.Add("V1+V2");
            }

            this.DisplayName += " (" + string.Join(", ", titleAppends) + ")";

            return discovery;
        }
    }
}
