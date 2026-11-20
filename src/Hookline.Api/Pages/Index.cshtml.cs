using Hookline.Api.Dashboard;
using Hookline.Api.Data;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hookline.Api.Pages;

public sealed class IndexModel(DashboardData data) : PageModel
{
    public IReadOnlyDictionary<DeliveryStatus, int> Counts { get; private set; } = new Dictionary<DeliveryStatus, int>();

    public IReadOnlyList<DeliveryRow> Rows { get; private set; } = [];

    public DeliveryStatus? Status { get; private set; }

    public DateTimeOffset Now => data.Now;

    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(string? status, CancellationToken cancellationToken)
    {
        if (status is not null)
        {
            if (!Enum.TryParse<DeliveryStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return NotFound();
            }

            Status = parsed;
        }

        Counts = await data.CountsAsync(cancellationToken);
        Rows = await data.ListAsync(Status, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostSendAgainAsync(string id, string? status, CancellationToken cancellationToken)
    {
        Message = Display.SendAgain(await data.SendAgainAsync(id, cancellationToken));
        return RedirectToPage(new { status });
    }
}
