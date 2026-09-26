using BPUtil;
using BPUtil.MVC;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KVStore.Controllers;

namespace KVStore.Controllers
{
	/// <summary>
	/// Contains methods intended to be used by the Linux command line interface.
	/// </summary>
	public class CommandLineInterface : AdminConsoleControllerBase
	{
		public ActionResult ReadConfig()
		{
			try
			{
				return Json(new CLIResponse(JsonConvert.SerializeObject(KVStoreService.MakeLocalSettingsReference(), Formatting.Indented)));
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		public ActionResult LoadConfig()
		{
			try
			{
				KVStoreService.InitializeSettings();
				return Json(new CLIResponse("Success"));
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		public async Task<ActionResult> SaveConfig()
		{
			try
			{
				await KVStoreService.SaveNewSettings(KVStoreService.CloneSettingsObjectSlow(), CancellationToken).ConfigureAwait(false);
				return Json(new CLIResponse("Success"));
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
		/// <summary>
		/// Deletes one item identified by bucket and key, for takedown requests.
		/// </summary>
		public async Task<ActionResult> DeleteItem()
		{
			try
			{
				DeleteItemRequest request = await ParseRequest<DeleteItemRequest>(CancellationToken).ConfigureAwait(false);
				return Json(new CLIResponse(Operations.DeleteItemInternal(request)));
			}
			catch (Exception ex)
			{
				return ApiError(ex.FlattenMessages());
			}
		}
	}
	public class CLIResponse : ApiResponseBase
	{
		public string message;
		public CLIResponse(string message) : base(true)
		{
			this.message = message;
		}
	}
}
