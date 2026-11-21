using Tandem.App.Commands;

namespace Tandem.App.Tests.Commands;

public class CommandRegistryTests
{
    [Fact]
    public async Task Execute_runs_the_handler()
    {
        var registry = new CommandRegistry();
        var runs = 0;
        registry.Register("x", () => { runs++; return Task.CompletedTask; });

        Assert.True(await registry.ExecuteAsync("x"));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Execute_respects_canExecute()
    {
        var registry = new CommandRegistry();
        var allowed = false;
        var runs = 0;
        registry.Register("x", () => { runs++; return Task.CompletedTask; }, () => allowed);

        Assert.False(registry.CanExecute("x"));
        Assert.False(await registry.ExecuteAsync("x"));
        Assert.Equal(0, runs);

        allowed = true;
        Assert.True(registry.CanExecute("x"));
        Assert.True(await registry.ExecuteAsync("x"));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Unknown_command_returns_false()
    {
        var registry = new CommandRegistry();

        Assert.False(registry.CanExecute("nope"));
        Assert.False(await registry.ExecuteAsync("nope"));
    }

    [Fact]
    public async Task Register_again_replaces_the_handler()
    {
        var registry = new CommandRegistry();
        var calls = new List<string>();
        registry.Register("x", () => { calls.Add("first"); return Task.CompletedTask; }, () => false);
        registry.Register("x", () => { calls.Add("second"); return Task.CompletedTask; });

        Assert.True(await registry.ExecuteAsync("x"));
        Assert.Equal(["second"], calls);
    }

    [Fact]
    public async Task Execute_awaits_the_handler()
    {
        var registry = new CommandRegistry();
        var gate = new TaskCompletionSource();
        var finished = false;
        registry.Register("x", async () => { await gate.Task; finished = true; });

        var execution = registry.ExecuteAsync("x");
        Assert.False(execution.IsCompleted);

        gate.SetResult();
        Assert.True(await execution);
        Assert.True(finished);
    }

    [Fact]
    public async Task Handler_exception_propagates_to_the_caller()
    {
        var registry = new CommandRegistry();
        registry.Register("x", () => throw new InvalidOperationException("boom"));
        registry.Register("y", () => Task.FromException(new InvalidOperationException("async boom")));

        Assert.Equal("boom", (await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("x"))).Message);
        Assert.Equal("async boom",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("y"))).Message);
    }

    [Fact]
    public async Task Command_can_run_again_after_its_handler_failed()
    {
        var registry = new CommandRegistry();
        var fail = true;
        registry.Register("x", () => fail ? throw new InvalidOperationException() : Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteAsync("x"));
        fail = false;

        Assert.True(await registry.ExecuteAsync("x"));
    }

    [Fact]
    public async Task Same_command_does_not_run_twice_while_its_handler_is_running()
    {
        var registry = new CommandRegistry();
        var gate = new TaskCompletionSource();
        var runs = 0;
        registry.Register("x", async () => { runs++; await gate.Task; });

        var first = registry.ExecuteAsync("x");
        Assert.False(await registry.ExecuteAsync("x"));

        gate.SetResult();
        Assert.True(await first);
        Assert.Equal(1, runs);

        Assert.True(await registry.ExecuteAsync("x"));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Other_commands_run_while_one_is_running()
    {
        var registry = new CommandRegistry();
        var gate = new TaskCompletionSource();
        var otherRuns = 0;
        registry.Register("slow", () => gate.Task);
        registry.Register("other", () => { otherRuns++; return Task.CompletedTask; });

        var slow = registry.ExecuteAsync("slow");
        Assert.True(await registry.ExecuteAsync("other"));
        Assert.Equal(1, otherRuns);

        gate.SetResult();
        Assert.True(await slow);
    }

    [Fact]
    public void RaiseCanExecuteChanged_raises_the_event()
    {
        var registry = new CommandRegistry();
        object? sender = null;
        registry.CanExecuteChanged += (s, _) => sender = s;

        registry.RaiseCanExecuteChanged();

        Assert.Same(registry, sender);
    }

    [Fact]
    public void A_menu_item_of_an_unregistered_command_is_disabled_and_of_a_registered_one_enabled()
    {
        // Menu items run their command through RunCommand with the id as the parameter (MainMenu.CommandItem).
        var registry = new CommandRegistry();
        var runs = new List<string>();
        var command = new RunCommand(registry, id =>
        {
            runs.Add(id);
            return Task.CompletedTask;
        });
        var changes = 0;
        command.CanExecuteChanged += (_, _) => changes++;

        Assert.False(command.CanExecute("made.up"));
        command.Execute("made.up");
        Assert.Empty(runs);

        registry.Register("made.up", () => Task.CompletedTask);
        registry.RaiseCanExecuteChanged();

        Assert.Equal(1, changes);
        Assert.True(command.CanExecute("made.up"));
        command.Execute("made.up");
        Assert.Equal(["made.up"], runs);
    }
}
