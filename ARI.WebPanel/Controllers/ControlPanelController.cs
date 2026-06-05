using Microsoft.AspNetCore.Mvc;

namespace ARI.WebPanel.Controllers;

public class ControlPanelController : Controller
{
    public IActionResult Index() => View();
}
