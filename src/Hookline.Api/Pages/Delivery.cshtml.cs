using Hookline.Api.Dashboard;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Hookline.Api.Pages;

public sealed class DeliveryModel(DashboardData data) : PageModel
{
    public DeliveryDetail Detail { get; private set; } = null!;

    public DateTimeOffset Now => data.Now;

    [TempData]
    public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(string id, CancellationToken cancellationToken)
    {
        var detail = await data.FindAsync(id, cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }

    public async Task<IActionResult> OnPostSendAgainAsync(string id, CancellationToken cancellationToken)
    {
        Message = Display.SendAgain(await data.SendAgainAsync(id, cancellationToken));
        return RedirectToPage(new { id });
    }
}
