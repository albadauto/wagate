using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaGate.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClientTokenEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClientTokenEnabled",
                table: "Accounts",
                type: "bit",
                nullable: false,
                defaultValue: true); // contas existentes continuam exigindo o token
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientTokenEnabled",
                table: "Accounts");
        }
    }
}
