using System.Security.Claims;

using Hookline.Api.Dashboard;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Hookline.Api.Pages;

public sealed class LoginModel(IOptions<DashboardOptions> options) : PageModel
{
    public bool Enabled => options.Value.Enabled;

    [BindProperty]
    public string? Password { get; set; }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if (!options.Value.Matches(Password))
        {
            Error = Enabled ? "That isn't the admin password." : null;
            return Page();
        }

        var admin = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(admin));
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl : "/");
    }
}
