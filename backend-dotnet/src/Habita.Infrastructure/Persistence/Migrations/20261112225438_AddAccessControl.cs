using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Habita.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommunityId",
                table: "Recordings",
                type: "varchar(32)",
                unicode: false,
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "Communities",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Communities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    CommunityId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    PersonId = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Role = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => new { x.CommunityId, x.PersonId, x.Role });
                    table.ForeignKey(
                        name: "FK_Memberships_Communities_CommunityId",
                        column: x => x.CommunityId,
                        principalTable: "Communities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Memberships_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_CommunityId",
                table: "Recordings",
                column: "CommunityId");

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_PersonId",
                table: "Memberships",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_People_Email",
                table: "People",
                column: "Email",
                unique: true,
                filter: "[Email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_People_ExternalId",
                table: "People",
                column: "ExternalId",
                unique: true,
                filter: "[ExternalId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "Communities");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_CommunityId",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "CommunityId",
                table: "Recordings");
        }
    }
}
