using Common.AspNetCore.Authentication;
using Common.Core.Domain;
using Microsoft.AspNetCore.Mvc;

namespace CustomerMgnt.Api.Controllers;

/// <summary>ฐานของ controller: JSON, ProblemDetails และผู้ใช้ปัจจุบันจาก JWT ของ AuthServer</summary>
[ApiController]
[Produces("application/json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status401Unauthorized)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
public abstract class CustomerMgntControllerBase : ControllerBase
{
    /// <summary>ผู้ใช้ปัจจุบัน (service layer ตรวจสิทธิ์ซ้ำทุก method — defense in depth)</summary>
    protected UserContextDTO CurrentUser => HttpContext.GetUserContext();
}
