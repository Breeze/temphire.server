using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TempHire.Model.Migrations.TempHire
{
    /// <summary>
    /// A phone number in one field: the area code moves into <c>Number</c>, which widens from 8 to
    /// 20 characters, and the <c>AreaCode</c> column goes.  A number that was 7 digits (with or
    /// without a dash or dot after the first 3) under a 3-digit area code becomes
    /// <c>(206) 555-1234</c>, the format the clients write; anything else keeps both parts as they
    /// were, separated by a space, for the user to correct.
    /// </summary>
    public partial class PhoneNumberInOneField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "PhoneNumbers",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(8)",
                oldMaxLength: 8);

            migrationBuilder.Sql("""
                UPDATE p SET p.Number =
                    CASE WHEN p.AreaCode LIKE N'[0-9][0-9][0-9]'
                              AND LEN(d.Digits) = 7 AND d.Digits NOT LIKE N'%[^0-9]%'
                         THEN N'(' + p.AreaCode + N') ' + LEFT(d.Digits, 3) + N'-' + RIGHT(d.Digits, 4)
                         ELSE LTRIM(p.AreaCode + N' ' + p.Number)
                    END
                FROM PhoneNumbers p
                CROSS APPLY (SELECT REPLACE(REPLACE(p.Number, N'-', N''), N'.', N'') AS Digits) d;
                """);

            migrationBuilder.DropColumn(
                name: "AreaCode",
                table: "PhoneNumbers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AreaCode",
                table: "PhoneNumbers",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "");

            // Splits what Up wrote, and any other 10-digit number in a format the rule accepts; a
            // number it can't split keeps its first 8 characters and an empty area code.
            migrationBuilder.Sql("""
                UPDATE p SET p.AreaCode = LEFT(d.Digits, 3), p.Number = SUBSTRING(d.Digits, 4, 3) + N'-' + RIGHT(d.Digits, 4)
                FROM PhoneNumbers p
                CROSS APPLY (SELECT RIGHT(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                    p.Number, N'(', N''), N')', N''), N'-', N''), N'.', N''), N' ', N''), N'+', N''), 10) AS Digits) d
                WHERE LEN(d.Digits) = 10 AND d.Digits NOT LIKE N'%[^0-9]%';

                UPDATE PhoneNumbers SET Number = LEFT(Number, 8) WHERE LEN(Number) > 8;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Number",
                table: "PhoneNumbers",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }
    }
}
