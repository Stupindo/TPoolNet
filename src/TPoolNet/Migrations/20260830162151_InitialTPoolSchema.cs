using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TPoolNet.Migrations
{
    /// <inheritdoc />
    public partial class InitialTPoolSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tpool");

            migrationBuilder.CreateTable(
                name: "TablesConsumerType",
                schema: "tpool",
                columns: table => new
                {
                    ConsumerTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ConsumerName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TablesConsumerType", x => x.ConsumerTypeId);
                });

            migrationBuilder.CreateTable(
                name: "TablesType",
                schema: "tpool",
                columns: table => new
                {
                    TableTypeId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TypeName = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    TablePrefix = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    DdlTemplate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MaxPoolSize = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TablesType", x => x.TableTypeId);
                });

            migrationBuilder.CreateTable(
                name: "TablesUsageHistory",
                schema: "tpool",
                columns: table => new
                {
                    HistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TablePoolId = table.Column<long>(type: "bigint", nullable: false),
                    ConsumerId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    BookedAtUtc = table.Column<DateTime>(type: "datetime2(2)", precision: 2, nullable: false),
                    ReleasedAtUtc = table.Column<DateTime>(type: "datetime2(2)", precision: 2, nullable: false),
                    ReleaseReason = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, defaultValue: "Explicit")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TablesUsageHistory", x => x.HistoryId);
                });

            migrationBuilder.CreateTable(
                name: "TablesPool",
                schema: "tpool",
                columns: table => new
                {
                    TablePoolId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TableTypeId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "sysname", maxLength: 128, nullable: false, defaultValue: "tpool"),
                    TableName = table.Column<string>(type: "sysname", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TablesPool", x => x.TablePoolId);
                    table.ForeignKey(
                        name: "FK_TablesPool_TablesType_TableTypeId",
                        column: x => x.TableTypeId,
                        principalSchema: "tpool",
                        principalTable: "TablesType",
                        principalColumn: "TableTypeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TablesUsage",
                schema: "tpool",
                columns: table => new
                {
                    TablePoolId = table.Column<long>(type: "bigint", nullable: false),
                    ConsumerId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    BookedAtUtc = table.Column<DateTime>(type: "datetime2(2)", precision: 2, nullable: false),
                    HeartbeatUtc = table.Column<DateTime>(type: "datetime2(2)", precision: 2, nullable: false),
                    DeadlineUtc = table.Column<DateTime>(type: "datetime2(2)", precision: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TablesUsage", x => x.TablePoolId);
                    table.ForeignKey(
                        name: "FK_TablesUsage_TablesPool_TablePoolId",
                        column: x => x.TablePoolId,
                        principalSchema: "tpool",
                        principalTable: "TablesPool",
                        principalColumn: "TablePoolId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TablesPool_SchemaName_TableName",
                schema: "tpool",
                table: "TablesPool",
                columns: new[] { "SchemaName", "TableName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TablesPool_TableTypeId",
                schema: "tpool",
                table: "TablesPool",
                column: "TableTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_TablesType_TypeName",
                schema: "tpool",
                table: "TablesType",
                column: "TypeName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TablesUsageHistory_BookedAtUtc",
                schema: "tpool",
                table: "TablesUsageHistory",
                column: "BookedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TablesUsageHistory_TablePoolId",
                schema: "tpool",
                table: "TablesUsageHistory",
                column: "TablePoolId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TablesConsumerType",
                schema: "tpool");

            migrationBuilder.DropTable(
                name: "TablesUsage",
                schema: "tpool");

            migrationBuilder.DropTable(
                name: "TablesUsageHistory",
                schema: "tpool");

            migrationBuilder.DropTable(
                name: "TablesPool",
                schema: "tpool");

            migrationBuilder.DropTable(
                name: "TablesType",
                schema: "tpool");
        }
    }
}
