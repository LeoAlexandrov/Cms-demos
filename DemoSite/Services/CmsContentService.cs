using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using HCms.Content.Repo;
using HCms.Content.ViewModels;
using HCms.Dto;


namespace DemoSite.Services
{

	/// <summary>
	/// This service provides methods to access CMS content via injected content-repository <see cref="IContentRepo"/>
	/// with additional features like caching and authorization checks. Must be registered as a scoped service.
	/// </summary>
	public class CmsContentService(
		IContentRepo repo, 
		IMemoryCache cache, 
		IAuthorizationService authorizationService,
		ILogger<CmsContentService> logger)
	{
		const string EVENT_DOC_CREATE = "on_doc_create";
		const string EVENT_DOC_CHANGE = "on_doc_change";
		const string EVENT_DOC_UPDATE = "on_doc_update";
		const string EVENT_DOC_DELETE = "on_doc_delete";
		const string EVENT_USERS_CHANGE = "on_users_change";
		const string EVENT_XMLSCHEMA = "on_xmlschema_change";
		const string EVENT_ENABLE = "on_destination_enable";
		const string EVENT_DISABLE = "on_destination_disable";


		readonly IContentRepo _repo = repo;
		readonly IMemoryCache _cache = cache;
		readonly IAuthorizationService _authorizationService = authorizationService;
		readonly ILogger<CmsContentService> _logger = logger;

		public Document RequestedDocument { get; private set; }
		public IContentRepo Repo { get => _repo; }
		public IMemoryCache Cache { get => _cache; }

		public enum AuthResult
		{
			Success,
			Unauthorized,
			Forbidden
		}

		/// <summary>
		/// Evaluates whether the specified user satisfies the given authorization policies.
		/// </summary>
		/// <remarks>This method evaluates the specified policies sequentially. If <paramref name="all"/> is <see langword="true"/>, 
		/// the evaluation stops as soon as a policy fails. If <paramref name="all"/> is <see langword="false"/>,
		/// the evaluation stops as soon as a policy succeeds.</remarks>
		/// <param name="user">The <see cref="ClaimsPrincipal"/> representing the user to be authorized.</param>
		/// <param name="policies">An array of policy names to evaluate against the user. Each policy name must be non-null and non-empty.</param>
		/// <param name="authorizationService">The <see cref="IAuthorizationService"/> used to evaluate the policies.</param>
		/// <param name="all">A boolean value indicating whether all policies must be satisfied for the authorization to succeed. 
		/// If <see langword="true"/>, the user must satisfy all policies; if <see langword="false"/>, satisfying any one policy is sufficient.</param>
		/// <returns>A <see cref="Task{TResult}"/> that represents the asynchronous operation. The task result is <see
		/// langword="true"/> if the user satisfies the required policies; otherwise, <see langword="false"/>.</returns>
		static Task<bool> Authorize(ClaimsPrincipal user, string[] policies, IAuthorizationService authorizationService, bool all)
		{
			Task<bool> taskChain = Task.FromResult(all);

			foreach (var policy in policies)
			{
				taskChain = taskChain
					.ContinueWith(async previousTask =>
					{
						if (previousTask.Result ^ all)
							return !all;

						bool success;

						try
						{
							var result = await authorizationService.AuthorizeAsync(user, policy.Trim());
							success = result.Succeeded;
						}
						catch
						{
							success = false;
						}

						return success;
					})
					.Unwrap();
			}

			return taskChain;
		}

		/// <summary>
		/// Authorizes the specified user basing on the authentication and authorization requirements of the requested document.
		/// </summary>
		/// <remarks>This method evaluates the authentication state of the user and checks whether the user satisfies 
		/// the authorization policies defined for the requested document. If no authorization is required for the document,
		/// the method returns <see cref="AuthResult.Success"/> immediately.</remarks>
		/// <param name="user">The <see cref="ClaimsPrincipal"/> representing the user to authorize.</param>
		/// <returns>An <see cref="AuthResult"/> indicating the outcome of the authorization process.</returns>
		public async ValueTask<AuthResult> Authorize(ClaimsPrincipal user)
		{
			if (this.RequestedDocument == null || !this.RequestedDocument.AuthRequired)
				return AuthResult.Success;

			if (user.Identity?.IsAuthenticated != true)
				return AuthResult.Unauthorized;

			string policies = this.RequestedDocument.AuthPolicies;
			int commaIdx = policies.IndexOf(',');
			int semicolonIdx = policies.IndexOf(';');
			bool result;

			if (commaIdx == -1 && semicolonIdx == -1)
				result = await Authorize(user, [policies], _authorizationService, true);
			else if (commaIdx == -1 || commaIdx > semicolonIdx)
				result = await Authorize(user, policies.Split(';', StringSplitOptions.RemoveEmptyEntries), _authorizationService, false);
			else if (semicolonIdx == -1 || commaIdx < semicolonIdx)
				result = await Authorize(user, policies.Split(',', StringSplitOptions.RemoveEmptyEntries), _authorizationService, true);
			else
				result = false;

			return result ? AuthResult.Success : AuthResult.Forbidden;
		}

		/// <summary>
		/// Authorizes authenticated user as CMS editor basing on the provided user claims.
		/// </summary>
		/// <remarks>This method checks the user's authentication status and retrieves their role from the repository
		/// to determine authorization. Ensure that the <paramref name="user"/> parameter contains valid claims, including a
		/// <see cref="ClaimTypes.NameIdentifier"/> claim.</remarks>
		/// <param name="user">The <see cref="ClaimsPrincipal"/> representing the user to authorize.</param>
		/// <returns>A <see cref="ValueTask{TResult}"/> that resolves to an <see cref="AuthResult"/> indicating the 
		/// authorization outcome: <see cref="AuthResult.Unauthorized"/> if the user is not authenticated, 
		/// <see cref="AuthResult.Forbidden"/> if the user lacks a valid login or role, or <see cref="AuthResult.Success"/> if the user is authorized.</returns>
		async ValueTask<AuthResult> AuthorizeEditor(ClaimsPrincipal user)
		{
			if (!user.Identity.IsAuthenticated)
				return AuthResult.Unauthorized;

			string login = user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

			if (string.IsNullOrEmpty(login))
				return AuthResult.Forbidden;

			string role = await _repo.UserRole(login);

			return string.IsNullOrEmpty(role) ? AuthResult.Forbidden : AuthResult.Success;
		}

		/// <summary>
		/// Retrieves a document from the content repository by its root and path along with children and siblings, basing on the user's authorization level.
		/// </summary>
		/// <remarks>The user's authorization level determines which document statuses are accessible. Authenticated users which exist in the CMS database
		/// can access documents with statuses 'published' and 'inReview', while other users can only access published documents.</remarks>
		/// <param name="cmsRoot">The root where to search the document.</param>
		/// <param name="cmsPath">The path to the document.</param>
		/// <param name="childPos">The zero-based index of the first child document to include, used for pagination. No child documents included if index is negative.</param>
		/// <param name="takeChildren">The maximum number of child documents to include.</param>
		/// <param name="user">The user whose authorization level determines ability to access content of documents with status that differs from 'published'.</param>
		/// <returns>A <see cref="Document"/> object representing the requested document, including its children and siblings.
		/// Returns <c>null</c> if the document is not found or the user lacks sufficient permissions.</returns>
		public async Task<Document> GetDocument(string cmsRoot, string cmsPath, int childPos, int takeChildren, ClaimsPrincipal user)
		{
			int[] allowedStatus = await AuthorizeEditor(user) == AuthResult.Success ? 
				[1, 2] : // published and drafts with 'InReview' status 
				[1]; // only published documents

			var doc = await _repo.GetDocument(cmsRoot, cmsPath, childPos, takeChildren, true, allowedStatus, false);

			RequestedDocument = doc;

			return doc;
		}

		/// <summary>
		/// Updates the cache basing on notication event payload. Used by <see cref="EventSubscriptionService"/>
		/// </summary>
		/// <param name="model">The event payload containing the event type and any associated data. 
		/// The <see cref="EventPayload.Event"/> property determines the type of cache update to perform.</param>
		public void UpdateCache(EventPayload model)
		{
			switch (model.Event)
			{
				case EVENT_XMLSCHEMA:

					if (_repo is SqlContentRepo)
						_repo.Reset();

					_logger.LogInformation("Schemata have been reloaded");
					break;

				case EVENT_USERS_CHANGE:

					if (_repo is RemoteContentRepo)
						_repo.Reset();

					_logger.LogInformation("User roles cache has been cleared");
					break;

				case EVENT_DOC_UPDATE:

					if (model.AffectedContent != null)
					{
						string path;
						string root;

						foreach (var con in model.AffectedContent)
						{
							path = string.IsNullOrEmpty(con.Path) ? "/" : con.Path;
							root = con.Root;

							_cache.Remove($"{root}-dark-{path}");
							_cache.Remove($"{root}-light-{path}");

							_logger.LogInformation("Cache record for '{root}-dark&light-{path}' has been removed", root, path);
						}

						return;
					}

					break;

				default:
					break;
			}


			if (_cache is MemoryCache memoryCache)
			{
				memoryCache.Clear();

				_logger.LogInformation("Entire cache has been cleared");
			}
		}

	}

}