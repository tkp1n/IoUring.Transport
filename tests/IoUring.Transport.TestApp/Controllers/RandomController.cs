using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace IoUring.TestApp.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class RandomController : ControllerBase
    {
        private readonly Task<string> t = Task.FromResult("Hello");

        public RandomController()
        {
        }

        [HttpGet]
        public Task<string> Get()
        {
            return t;
        }
    }
}