namespace Vigiloom.Testing.SqlServer;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Test runner uses a child process so termination can be exercised without killing its host.
        if (args is ["hold"])
        {
            Console.WriteLine(SqlTestDatabase.RunManifestPath);
            await Console.Out.FlushAsync();
            if (await Console.In.ReadLineAsync() != "start") return 1;
            await using var database = await SqlTestDatabase.CreateAsync(TestDatabaseServer.FromEnvironment());
            Console.WriteLine(database.ManifestPath);
            await Console.Out.FlushAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }

        if (args.Length != 2 || args[0] is not ("recover" or "recover-run"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/Vigiloom.Testing.SqlServer -- <recover|recover-run> <manifest-path>");
            return 2;
        }

        try
        {
            if (args[0] == "recover-run")
                await SqlTestDatabase.RecoverRunAsync(args[1], TestDatabaseServer.FromEnvironment());
            else
                await SqlTestDatabase.RecoverAsync(args[1], TestDatabaseServer.FromEnvironment());
            Console.WriteLine("Test database cleanup completed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Cleanup failed: {exception.Message}");
            return 1;
        }
    }
}
