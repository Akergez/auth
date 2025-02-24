using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Serialization;
using PhotosService.Data;
using PhotosService.Models;
using PhotosService.Services;
using Serilog;

namespace PhotosService
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

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddRazorPages();
            services.AddControllers(options =>
            {
                options.ReturnHttpNotAcceptable = true;
                // NOTE: Существенно, что новый провайдер добавляется в начало списка перед провайдером по умолчанию
                options.ModelBinderProviders.Insert(0, new JwtSecurityTokenModelBinderProvider());
            })
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            });

            var connectionString = configuration.GetConnectionString("PhotosDbContextConnection")
                ?? "Data Source=PhotosService.db";
            services.AddDbContext<PhotosDbContext>(o => o.UseSqlite(connectionString));

            services.AddScoped<IPhotosRepository, LocalPhotosRepository>();

            services.AddAutoMapper(cfg =>
            {
                cfg.CreateMap<PhotoEntity, PhotoDto>().ReverseMap();
            }, new System.Reflection.Assembly[0]);
            
            services.AddAuthentication("Bearer")
                .AddJwtBearer("Bearer", options =>
                {
                    const string authority = "https://localhost:7001";
                    const string apiResourceId = "photos_service";
                    const string apiResourceSecret = "photos_service_secret";

                    options.Authority = authority;
                    options.Audience = apiResourceId;

                    options.SecurityTokenValidators.Clear();
                    options.SecurityTokenValidators.Add(new IntrospectionSecurityTokenValidator(
                        authority, apiResourceId, apiResourceSecret));

                    options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
                    options.Events = new JwtBearerEvents
                    {
                        OnTokenValidated = context =>
                        {
                            JwtSecurityTokenModelBinder.SaveToken(context.HttpContext, context.SecurityToken);
                            return Task.CompletedTask;
                        }
                    };
                });
            
            services.AddCors(options =>
            {
                options.AddDefaultPolicy(
                    builder =>
                    {
                        builder.WithOrigins("https://localhost:8001")
                            .AllowAnyHeader()
                            .AllowAnyMethod();
                    });
            });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            app.UseHttpsRedirection();

            app.UseSerilogRequestLogging();

            app.UseRouting();
            app.UseCors();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute("default", "{controller=Photos}/{action=Index}/{id?}");
                endpoints.MapRazorPages();
            });
        }
    }
}
