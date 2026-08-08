using KSubMaker.Application.Abstractions;
using KSubMaker.Worker.Process;
using KSubMaker.Worker.Processing;
using KSubMaker.Worker.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KSubMaker.Worker;

/// <summary>Composition root for the worker host.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers tool discovery, the worker process client and the job processors.
    ///
    /// Call order matters: <c>InProcessJobProcessor</c> and <see cref="IAppPaths"/> come from
    /// Infrastructure/Application, so <c>AddKSubMakerInfrastructure()</c> must have run first. Only
    /// <see cref="IJobProcessorSelector"/> is resolved by the rest of the app; both processors are
    /// registered concretely so the selector can hand out whichever one the settings ask for.
    /// </summary>
    public static IServiceCollection AddKSubMakerWorker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WorkerOptions>();

        // One instance, three faces: IToolLocator is the Application-layer contract, the concrete type
        // and IWorkerLaunchDescriptor carry the extra launch metadata the host needs.
        services.TryAddSingleton<ToolLocator>();
        services.TryAddSingleton<IToolLocator>(static sp => sp.GetRequiredService<ToolLocator>());
        services.TryAddSingleton<IWorkerLaunchDescriptor>(static sp => sp.GetRequiredService<ToolLocator>());

        // Singleton: one Python process is reused for every job, and the container's disposal of this
        // singleton is what shuts the process tree down on application exit.
        services.TryAddSingleton<IWorkerClient, WorkerProcessClient>();

        // The authoritative CUDA answer. Registered here rather than in Infrastructure because only
        // this layer can talk to the Python process; HardwareService takes it as an optional
        // dependency so the Application layer still builds without a worker.
        services.TryAddSingleton<IWorkerHardwareProbe, WorkerHardwareProbe>();

        services.TryAddSingleton<WorkerJobProcessor>();
        services.TryAddSingleton<IJobProcessorSelector, JobProcessorSelector>();

        return services;
    }
}
