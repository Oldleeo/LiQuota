using CodexQuotaWidget;
using System.Text.Json;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

using var rateDocument = JsonDocument.Parse("""
{
  "accountId": "acct_test",
  "rateLimitsByLimitId": {
    "codex": {
      "limitName": "codex",
      "planType": "plus",
      "primary": { "usedPercent": 37.5, "windowDurationMins": 300, "resetsAt": 1893456000 },
      "secondary": { "usedPercent": 11, "windowDurationMins": 10080, "resetsAt": 1894060800 }
    },
    "review": {
      "limitName": "Code review",
      "primary": { "usedPercent": 5, "windowDurationMins": 1440, "resetsAt": 1893456000 }
    }
  },
  "rateLimitResetCredits": { "availableCount": 2 }
}
""");
using var accountDocument = JsonDocument.Parse("""
{
  "account": {
    "type": "chatgpt",
    "email": "li@example.com",
    "planType": "pro"
  }
}
""");

var snapshot = CodexAppServerClient.ParseSnapshot(rateDocument.RootElement, accountDocument.RootElement);
Assert(snapshot.Windows.Count == 3, "Expected all primary and secondary quota windows.");
Assert(snapshot.Windows[0].WindowDurationMins == 300, "Quota windows must be sorted by duration.");
Assert(snapshot.Windows[0].Title == "5 小时额度", "A 300-minute quota must be labelled as 5 hours.");
Assert(Math.Abs(snapshot.Windows[0].RemainingPercent - 62.5) < 0.001, "Remaining percentage is incorrect.");
Assert(snapshot.PlanType == "pro", "account/read plan should take precedence.");
Assert(snapshot.AccountEmail == "li@example.com", "Account email was not parsed.");
Assert(snapshot.AccountId == "acct_test", "Account id was not parsed.");
Assert(snapshot.ResetCredits == 2, "Reset credits were not parsed.");
Assert(MainViewModel.FormatAccount(snapshot.AccountEmail, snapshot.AccountType) == "当前账号  li***@example.com",
    "Account display should be privacy-masked.");

using var legacyDocument = JsonDocument.Parse("""
{
  "rateLimits": {
    "planType": "plus",
    "primary": { "usedPercent": 20, "windowDurationMins": 60 }
  }
}
""");
var legacy = CodexAppServerClient.ParseSnapshot(legacyDocument.RootElement);
Assert(legacy.Windows.Count == 1 && legacy.Windows[0].RemainingPercent == 80,
    "Legacy rateLimits payload should remain supported.");

Console.WriteLine("LiQuota smoke tests passed.");
