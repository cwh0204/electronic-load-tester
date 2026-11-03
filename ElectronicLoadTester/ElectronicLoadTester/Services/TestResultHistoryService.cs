using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using ElectronicLoadTester.Models;
using Microsoft.Data.Sqlite;

namespace ElectronicLoadTester.Services
{
    public class TestResultHistoryService : ITestResultHistoryService
    {
        private readonly string _connectionString;

        public TestResultHistoryService()
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, "test_history.db");
            _connectionString = $"Data Source={dbPath};";
        }

        private IDbConnection CreateConnection() => new SqliteConnection(_connectionString);

        #region [1. SQLite DB 및 테이블 DDL 초기화]
        public async Task InitializeDatabaseAsync()
        {
            using var conn = CreateConnection();

            const string ddl = """
                -- 1. 검사 총괄 마스터 테이블
                CREATE TABLE IF NOT EXISTS TestMaster (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    SerialNumber        TEXT NOT NULL UNIQUE,
                    TargetSystem        TEXT NOT NULL,
                    RailName            TEXT NOT NULL,
                    TestMode            TEXT NOT NULL,
                    FinalVerdict        TEXT NOT NULL,
                    TotalTestedCount    INTEGER NOT NULL DEFAULT 2,
                    PassCount           INTEGER NOT NULL DEFAULT 0,
                    FailCount           INTEGER NOT NULL DEFAULT 0,
                    TestedAt            TEXT NOT NULL,
                    TrendDataJson       TEXT NULL,
                    Notes               TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS IDX_TestMaster_TestedAt ON TestMaster(TestedAt);
                CREATE INDEX IF NOT EXISTS IDX_TestMaster_TargetSystem ON TestMaster(TargetSystem);
                CREATE INDEX IF NOT EXISTS IDX_TestMaster_FinalVerdict ON TestMaster(FinalVerdict);
                CREATE INDEX IF NOT EXISTS IDX_TestMaster_SerialNumber ON TestMaster(SerialNumber);

                -- 2. 세부 스텝 테이블 (1:N 매핑, CASCADE 삭제)
                CREATE TABLE IF NOT EXISTS TestStepDetail (
                    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    MasterId            INTEGER NOT NULL,
                    StepNo              INTEGER NOT NULL,
                    StepName            TEXT NOT NULL,
                    Mode                TEXT NOT NULL,
                    TargetCondition     TEXT NOT NULL,
                    SpecRange           TEXT NOT NULL,
                    MeasuredVoltage     REAL NOT NULL,
                    MeasuredCurrent     REAL NOT NULL,
                    Result              TEXT NOT NULL,
                    ExecutedAt          TEXT NOT NULL,
                    FOREIGN KEY(MasterId) REFERENCES TestMaster(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IDX_TestStepDetail_MasterId ON TestStepDetail(MasterId);
            """;

            await conn.ExecuteAsync(ddl);
            Debug.WriteLine("[SQLite] 데이터베이스 및 인덱스 초기화 완료");
        }
        #endregion

        #region [2. 시리얼 번호 자동 생성 (일련번호 순차 채번)]
        public async Task<string> GenerateNextSerialNumberAsync(string targetSystem, string railName)
        {
            using var conn = CreateConnection();

            // 당일 기준 해당 시스템-레일의 마지막 채번된 시퀀스 + 1 쿼리
            const string sql = """
                SELECT 
                    @System || '-' || @Rail || '-' || strftime('%Y%m%d', 'now', 'localtime') || '-' || 
                    printf('%03d', IFNULL(MAX(CAST(SUBSTR(SerialNumber, -3) AS INTEGER)), 0) + 1)
                FROM TestMaster
                WHERE SerialNumber LIKE @System || '-' || @Rail || '-' || strftime('%Y%m%d', 'now', 'localtime') || '-%';
            """;

            string? nextSn = await conn.ExecuteScalarAsync<string>(sql, new
            {
                System = targetSystem.ToUpper(),
                Rail = railName.ToUpper()
            });

            return nextSn ?? $"{targetSystem.ToUpper()}-{railName.ToUpper()}-{DateTime.Now:yyyyMMdd}-001";
        }
        #endregion

        #region [3. 결과 영구 저장 (Master + Detail 트랜잭션)]
        public async Task<long> SaveTestResultAsync(TestMasterEntity master, IEnumerable<TestStepDetailEntity> steps)
        {
            using var conn = (SqliteConnection)CreateConnection();
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();

            try
            {
                // 1. 마스터 데이터 Insert 후 자동 생성된 PK(Id) 즉시 회수
                const string insertMasterSql = """
                    INSERT INTO TestMaster (
                        SerialNumber, TargetSystem, RailName, TestMode, FinalVerdict,
                        TotalTestedCount, PassCount, FailCount, TestedAt, TrendDataJson, Notes
                    )
                    VALUES (
                        @SerialNumber, @TargetSystem, @RailName, @TestMode, @FinalVerdict,
                        @TotalTestedCount, @PassCount, @FailCount, @TestedAt, @TrendDataJson, @Notes
                    );
                    SELECT last_insert_rowid();
                """;

                long masterId = await conn.ExecuteScalarAsync<long>(insertMasterSql, master, transaction);
                master.Id = masterId;

                // 2. 디테일 레코드 리스트 바인딩 및 일괄 Insert
                const string insertDetailSql = """
                    INSERT INTO TestStepDetail (
                        MasterId, StepNo, StepName, Mode, TargetCondition,
                        SpecRange, MeasuredVoltage, MeasuredCurrent, Result, ExecutedAt
                    )
                    VALUES (
                        @MasterId, @StepNo, @StepName, @Mode, @TargetCondition,
                        @SpecRange, @MeasuredVoltage, @MeasuredCurrent, @Result, @ExecutedAt
                    );
                """;

                var stepList = steps.ToList();
                foreach (var step in stepList)
                {
                    step.MasterId = masterId;
                }

                await conn.ExecuteAsync(insertDetailSql, stepList, transaction);

                transaction.Commit();
                Debug.WriteLine($"[SQLite] 검사 결과 저장 성공: S/N={master.SerialNumber}, MasterId={masterId}");
                return masterId;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                Debug.WriteLine($"[SQLite] 저장 중 롤백 발생: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region [4. 다중 조건 이력 검색 (동적 SQL)]
        public async Task<IEnumerable<TestMasterEntity>> SearchHistoryAsync(TestHistoryFilter filter)
        {
            using var conn = CreateConnection();

            var sql = new StringBuilder("""
                SELECT 
                    Id, SerialNumber, TargetSystem, RailName, TestMode,
                    FinalVerdict, TotalTestedCount, PassCount, FailCount,
                    TestedAt, Notes
                FROM TestMaster
                WHERE TestedAt >= @StartDate AND TestedAt <= @EndDate
            """);

            var param = new DynamicParameters();
            param.Add("StartDate", filter.StartDate.ToString("yyyy-MM-dd 00:00:00"));
            param.Add("EndDate", filter.EndDate.ToString("yyyy-MM-dd 23:59:59"));

            if (!string.IsNullOrWhiteSpace(filter.TargetSystem) && filter.TargetSystem != "전체")
            {
                sql.Append(" AND TargetSystem = @TargetSystem");
                param.Add("TargetSystem", filter.TargetSystem);
            }

            if (!string.IsNullOrWhiteSpace(filter.FinalVerdict) && filter.FinalVerdict != "전체")
            {
                sql.Append(" AND FinalVerdict = @FinalVerdict");
                param.Add("FinalVerdict", filter.FinalVerdict);
            }

            if (!string.IsNullOrWhiteSpace(filter.SearchText))
            {
                sql.Append(" AND (SerialNumber LIKE @SearchText OR Notes LIKE @SearchText)");
                param.Add("SearchText", $"%{filter.SearchText.Trim()}%");
            }

            sql.Append(" ORDER BY Id DESC");

            return await conn.QueryAsync<TestMasterEntity>(sql.ToString(), param);
        }
        #endregion

        #region [5. 특정 검사 마스터 + 세부 스텝 + 파형 데이터 단건 조회]
        public async Task<TestMasterEntity?> GetMasterWithDetailsByIdAsync(long masterId)
        {
            using var conn = CreateConnection();

            const string masterSql = "SELECT * FROM TestMaster WHERE Id = @Id";
            var master = await conn.QuerySingleOrDefaultAsync<TestMasterEntity>(masterSql, new { Id = masterId });

            if (master == null) return null;

            const string detailsSql = "SELECT * FROM TestStepDetail WHERE MasterId = @MasterId ORDER BY StepNo ASC";
            var details = await conn.QueryAsync<TestStepDetailEntity>(detailsSql, new { MasterId = masterId });
            master.Steps = details.ToList();

            return master;
        }
        #endregion

        #region [6. 단건 삭제]
        public async Task<bool> DeleteTestResultAsync(long masterId)
        {
            using var conn = CreateConnection();
            const string sql = "DELETE FROM TestMaster WHERE Id = @Id";
            int affected = await conn.ExecuteAsync(sql, new { Id = masterId });
            return affected > 0;
        }
        #endregion
    }
}