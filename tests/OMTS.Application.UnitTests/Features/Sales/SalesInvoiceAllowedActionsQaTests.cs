using OMTS.Application.Features.Sales.Invoices;
using OMTS.Domain.Constants;
using static OMTS.Application.Features.Sales.Invoices.SalesInvoiceAction;

namespace OMTS.Application.UnitTests.Features.Sales;

/// <summary>Independent QA of SALE-09.1 step 3: every role, state, delivery status, end of sale and return against an oracle written from the SRS text.</summary>
public class SalesInvoiceAllowedActionsQaTests
{
    public static TheoryData<string, bool, bool, bool, bool> Cases()
    {
        var data = new TheoryData<string, bool, bool, bool, bool>();
        foreach (var role in new[] { "Owner", "Manager", "Accountant", "ZoneManagerOfOtherZone" })
        {
            foreach (var cancelled in new[] { false, true })
            {
                foreach (var sending in new[] { false, true })
                {
                    foreach (var finished in new[] { false, true })
                    {
                        foreach (var hasReturn in new[] { false, true })
                        {
                            data.Add(role, cancelled, sending, finished, hasReturn);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_commands_match_the_srs(string role, bool cancelled, bool sending, bool finished, bool hasReturn)
    {
        var owner = role == "Owner";
        var managerOrOwner = owner || role == "Manager";
        var facts = new SalesInvoiceActionFacts(
            cancelled ? StatusIds.SalesInvoice.Cancelled : StatusIds.SalesInvoice.Completed,
            sending ? StatusIds.MessageDelivery.Sending : StatusIds.MessageDelivery.Sent,
            finished,
            hasReturn);
        var rights = new SalesInvoiceCallerRights(owner, managerOrOwner, managerOrOwner, managerOrOwner, owner, owner);

        var expected = new List<SalesInvoiceAction>();
        if (managerOrOwner && !cancelled && !sending && finished)
        {
            expected.Add(Resend);
        }

        if (managerOrOwner && !cancelled && !hasReturn)
        {
            expected.Add(CreateReturn);
        }

        if (owner && hasReturn)
        {
            expected.Add(CancelReturn);
        }

        if (owner && !cancelled && !hasReturn)
        {
            expected.Add(Edit);
            expected.Add(Correct);
            expected.Add(Cancel);
        }

        if (managerOrOwner)
        {
            expected.Add(Print);
        }

        Assert.Equal(expected, SalesInvoiceAllowedActions.Of(facts, rights));
    }
}
