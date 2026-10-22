using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaGate.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class BillingAndSignup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Accounts",
                type: "nvarchar(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CurrentPeriodEnd",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Document",
                table: "Accounts",
                type: "nvarchar(14)",
                maxLength: 14,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginAt",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockoutEnd",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Accounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Plan",
                table: "Accounts",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<int>(
                name: "PlanQuantity",
                table: "Accounts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SignupIp",
                table: "Accounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                table: "Accounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                table: "Accounts",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionStatus",
                table: "Accounts",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<DateTime>(
                name: "TermsAcceptedAt",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndsAt",
                table: "Accounts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrialUsed",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "StripeEvents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Document",
                table: "Accounts",
                column: "Document",
                unique: true,
                filter: "[Document] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_StripeCustomerId",
                table: "Accounts",
                column: "StripeCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_StripeSubscriptionId",
                table: "Accounts",
                column: "StripeSubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeEvents");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_Document",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_StripeCustomerId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_StripeSubscriptionId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "CurrentPeriodEnd",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Document",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "LockoutEnd",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Phone",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "Plan",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "PlanQuantity",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SignupIp",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "SubscriptionStatus",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "TermsAcceptedAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "TrialEndsAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "TrialUsed",
                table: "Accounts");
        }
    }
}
