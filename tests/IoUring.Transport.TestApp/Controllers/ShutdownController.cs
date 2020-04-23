using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;

namespace IoUring.TestApp.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ShutdownController : ControllerBase
    {
        private readonly IHostApplicationLifetime _lifetime;

        public ShutdownController(IHostApplicationLifetime lifetime)
        {
            _lifetime = lifetime;
        }

        [HttpGet]
        public string Get()
        {
            _lifetime.StopApplication();
            return "Shutting down";
        }
    }
}