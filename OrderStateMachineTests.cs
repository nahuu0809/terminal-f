using Xunit;
using FinancialTerminal.Core.Interfaces;

namespace FinancialTerminal.Tests;

public sealed class OrderStateMachineTests
{
    [Fact]
    public void Created_To_Filled_Valid_Path_Works()
    {
        var state = ManagedOrderState.Created;
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.ApproveRisk);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Submit);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Fill);

        Assert.Equal(ManagedOrderState.Filled, state);
    }

    [Fact]
    public void Created_To_Failed_Without_Submit_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OrderStateMachine.Transition(ManagedOrderState.Created, ManagedOrderSignal.Fail));
    }

    [Fact]
    public void RiskApproved_Can_Be_Cancelled()
    {
        var state = OrderStateMachine.Transition(ManagedOrderState.Created, ManagedOrderSignal.ApproveRisk);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Cancel);

        Assert.Equal(ManagedOrderState.Cancelled, state);
    }

    [Fact]
    public void Filled_State_Is_Terminal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OrderStateMachine.Transition(ManagedOrderState.Filled, ManagedOrderSignal.Cancel));
    }

    [Fact]
    public void Submitted_To_Partial_And_Then_Filled_Works()
    {
        var state = ManagedOrderState.Created;
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.ApproveRisk);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Submit);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.PartialFill);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Fill);

        Assert.Equal(ManagedOrderState.Filled, state);
    }

    [Fact]
    public void PartiallyFilled_Can_Be_Cancelled()
    {
        var state = ManagedOrderState.Created;
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.ApproveRisk);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Submit);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.PartialFill);
        state = OrderStateMachine.Transition(state, ManagedOrderSignal.Cancel);

        Assert.Equal(ManagedOrderState.Cancelled, state);
    }
}
