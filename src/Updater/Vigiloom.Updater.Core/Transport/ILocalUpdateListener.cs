using System.Threading.Channels;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Updater.Core.Transport;

/// <summary>Receives authenticated local requests for one Updater service.</summary>
public interface ILocalUpdateListener : IAsyncDisposable
{
    ChannelReader<UpdateHandoff> Requests { get; }
    Task RunAsync(CancellationToken token);
}
