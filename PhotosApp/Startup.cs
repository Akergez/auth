﻿using System.IdentityModel.Tokens.Jwt;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PhotosApp.Clients;
using PhotosApp.Clients.Models;
using PhotosApp.Data;
using PhotosApp.Models;
using PhotosApp.Services.Authorization;
using Serilog;

namespace PhotosApp
{
    public class Startup
    {
        private IWebHostEnvironment env { get; }
        private IConfiguration configuration { get; }

        public Startup(IWebHostEnvironment env, IConfiguration configuration)
        {
            this.env = env;
            this.configuration = configuration;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.Configure<PhotosServiceOptions>(configuration.GetSection("PhotosService"));

            var mvc = services.AddControllersWithViews();

            if (env.IsDevelopment())
                mvc.AddRazorRuntimeCompilation();

            // NOTE: Подключение IHttpContextAccessor, чтобы можно было получать HttpContext там,
            // где это не получается сделать более явно.
            services.AddHttpContextAccessor();

            var connectionString = configuration.GetConnectionString("PhotosDbContextConnection")
                                   ?? "Data Source=PhotosApp.db";
            services.AddDbContext<PhotosDbContext>(o => o.UseSqlite(connectionString));
            // NOTE: Вместо Sqlite можно использовать LocalDB от Microsoft или другой SQL Server
            //services.AddDbContext<PhotosDbContext>(o =>
            //    o.UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=PhotosApp;Trusted_Connection=True;"));

            // services.AddScoped<IPhotosRepository, LocalPhotosRepository>();
            services.AddScoped<IPhotosRepository, RemotePhotosRepository>();

            services.AddAutoMapper(cfg =>
            {
                cfg.CreateMap<PhotoEntity, PhotoDto>().ReverseMap();
                cfg.CreateMap<PhotoEntity, Photo>().ReverseMap();

                cfg.CreateMap<EditPhotoModel, PhotoEntity>()
                    .ForMember(m => m.FileName, options => options.Ignore())
                    .ForMember(m => m.Id, options => options.Ignore())
                    .ForMember(m => m.OwnerId, options => options.Ignore());
            }, new System.Reflection.Assembly[0]);

            services.AddTransient<ICookieManager, ChunkingCookieManager>();

            const string oidcAuthority = "https://localhost:7001";
            var oidcConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{oidcAuthority}/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever());
            services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(oidcConfigurationManager);
            
            services.AddAuthentication(options =>
                {
                    // NOTE: Схема, которую внешние провайдеры будут использовать для сохранения данных о пользователе
                    // NOTE: Так как значение совпадает с DefaultScheme, то эту настройку можно не задавать
                    options.DefaultSignInScheme = "Cookie";
                    // NOTE: Схема, которая будет вызываться, если у пользователя нет доступа
                    options.DefaultChallengeScheme = "Passport";
                    // NOTE: Схема на все остальные случаи жизни
                    options.DefaultScheme = "Cookie";
                })
                .AddCookie("Cookie", options =>
                {
                    // NOTE: Пусть у куки будет имя, которое расшифровывается на странице «Decode»
                    options.Cookie.Name = "PhotosApp.Auth";
                    // NOTE: Если не задать здесь путь до обработчика logout, то в этом обработчике
                    // будет игнорироваться редирект по настройке AuthenticationProperties.RedirectUri
                    options.LogoutPath = "/Passport/Logout";
                })
                .AddOpenIdConnect("Passport", "Паспорт", options =>
                {
                    options.ConfigurationManager = oidcConfigurationManager;
                    options.Authority = oidcAuthority;

                    options.ClientId = "Photos App by OIDC";
                    options.ClientSecret = "secret";
                    options.ResponseType = "code";

                    // NOTE: oidc и profile уже добавлены по умолчанию
                    options.Scope.Add("email");
                    options.Scope.Add("photos_app");
                    options.Scope.Add("photos");
                    options.Scope.Add("offline_access");

                    options.CallbackPath = "/signin-passport";
                    options.SignedOutCallbackPath = "/signout-callback-passport";

                    // NOTE: все эти проверки токена выполняются по умолчанию, указаны для ознакомления
                    options.TokenValidationParameters.ValidateIssuer = true; // проверка издателя
                    options.TokenValidationParameters.ValidateAudience = true; // проверка получателя
                    options.TokenValidationParameters.ValidateLifetime = true; // проверка не протух ли
                    options.TokenValidationParameters.RequireSignedTokens = true; // есть ли валидная подпись издателя
                    options.SaveTokens = true;
                    options.Events = new OpenIdConnectEvents
                    {
                        OnRemoteFailure = context => 
                        {
                            context.Response.Redirect("/");
                            context.HandleResponse();
                            return Task.CompletedTask;
                        },
                        OnTokenResponseReceived = context =>
                        {
                            var tokenResponse = context.TokenEndpointResponse;
                            var tokenHandler = new JwtSecurityTokenHandler();

                            SecurityToken accessToken = null;
                            if (tokenResponse.AccessToken != null)
                            {
                                accessToken = tokenHandler.ReadToken(tokenResponse.AccessToken);
                            }

                            SecurityToken idToken = null;
                            if (tokenResponse.IdToken != null)
                            {
                                idToken = tokenHandler.ReadToken(tokenResponse.IdToken);
                            }

                            string refreshToken = null;
                            if (tokenResponse.RefreshToken != null)
                            {
                                // NOTE: Это не JWT-токен
                                refreshToken = tokenResponse.RefreshToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });
            
            services.AddScoped<IAuthorizationHandler, MustOwnPhotoHandler>();
            services.AddAuthorization(options =>
            {
                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                options.AddPolicy(
                    "Beta",
                    policyBuilder =>
                    {
                        policyBuilder.RequireAuthenticatedUser();
                        policyBuilder.RequireClaim("testing", "beta");
                    });

                options.AddPolicy(
                    "CanAddPhoto",
                    policyBuilder =>
                    {
                        policyBuilder.RequireAuthenticatedUser();
                        policyBuilder.RequireClaim("subscription", "paid");
                    });
                options.AddPolicy(
                    "MustOwnPhoto",
                    policyBuilder =>
                    {
                        policyBuilder.RequireAuthenticatedUser();
                        policyBuilder.AddRequirements(new MustOwnPhotoRequirement());
                    });

                options.AddPolicy(
                    "OpenDecodePage",
                    policyBuilder =>
                    {
                        policyBuilder.RequireAuthenticatedUser();
                        policyBuilder.RequireRole("Dev");
                    });
            });
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
                app.UseExceptionHandler("/Exception");

            app.UseHttpsRedirection();
            app.UseStaticFiles();

            app.UseStatusCodePagesWithReExecute("/StatusCode/{0}");

            app.UseSerilogRequestLogging();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute("default", "{controller=Photos}/{action=Index}/{id?}");
            });
        }
    }
}