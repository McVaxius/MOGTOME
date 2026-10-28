using MOGTOME.Models;
using MOGTOME.Services.Keybound;

namespace MOGTOME.Tests;

public sealed class KeyboundRegistrationTests
{
    [Theory]
    [InlineData((int)BlundervilleRole.Solo)]
    [InlineData((int)BlundervilleRole.Leader)]
    [InlineData((int)BlundervilleRole.Member)]
    public void ReadyOpenDialogUsesCapturedCallbackWithOneIntZeroAndNoPriorNpcTarget(int role)
    {
        var calls = 0;
        var accepted = KeyboundLoopGame.SubmitRegistration(KeyboundEventLoop.Lobby, true, (BlundervilleRole)role,
            (name, updateState, values) =>
            {
                calls++;
                Assert.Equal("KTGEnterDialogue", name);
                Assert.True(updateState);
                Assert.Equal(0, Assert.IsType<int>(Assert.Single(values)));
                return true;
            });
        Assert.True(accepted);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(KeyboundEventLoop.Arena, true, (int)BlundervilleRole.Solo)]
    [InlineData(0u, true, (int)BlundervilleRole.Solo)]
    [InlineData(KeyboundEventLoop.Lobby, false, (int)BlundervilleRole.Solo)]
    [InlineData(KeyboundEventLoop.Lobby, true, (int)BlundervilleRole.Unknown)]
    public void UnreadyForeignOrUnknownRoleContextCannotSendRegistration(uint territory, bool ready, int role)
    {
        var calls = 0;
        Assert.False(KeyboundLoopGame.SubmitRegistration(territory, ready, (BlundervilleRole)role,
            (_, _, _) => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void NativeCallbackRejectionIsPropagated()
        => Assert.False(KeyboundLoopGame.SubmitRegistration(KeyboundEventLoop.Lobby, true,
            BlundervilleRole.Solo, (_, _, _) => false));
}
