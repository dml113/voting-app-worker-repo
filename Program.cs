using System;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Newtonsoft.Json;
using Npgsql;
using StackExchange.Redis;

namespace Worker
{
    public class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                // 로컬에 있는 인증서 경로 지정
                var caCertificatePath = "/app/rds-ca.pem";

                var pgsql = OpenDbConnection(
                    "Host=test-postgres.cde69tvxoswa.ap-northeast-2.rds.amazonaws.com;Username=postgres;Password=postgres;Database=postgres",
                    caCertificatePath);

                var redisConn = OpenRedisConnection("redis.wrwkvd.ng.0001.apn2.cache.amazonaws.com");
                var redis = redisConn.GetDatabase();

                var keepAliveCommand = pgsql.CreateCommand();
                keepAliveCommand.CommandText = "SELECT 1";

                var definition = new { vote = "", voter_id = "" };
                while (true)
                {
                    Thread.Sleep(100);

                    // Redis 재연결
                    if (redisConn == null || !redisConn.IsConnected)
                    {
                        Console.WriteLine("Reconnecting Redis");
                        redisConn = OpenRedisConnection("redis.wrwkvd.ng.0001.apn2.cache.amazonaws.com");
                        redis = redisConn.GetDatabase();
                    }

                    string json = redis.ListLeftPopAsync("votes").Result;
                    if (json != null)
                    {
                        var vote = JsonConvert.DeserializeAnonymousType(json, definition);
                        Console.WriteLine($"Processing vote for '{vote.vote}' by '{vote.voter_id}'");

                        // PostgreSQL 재연결
                        if (!pgsql.State.Equals(System.Data.ConnectionState.Open))
                        {
                            Console.WriteLine("Reconnecting DB");
                            pgsql = OpenDbConnection(
                                "Host=test-postgres.cde69tvxoswa.ap-northeast-2.rds.amazonaws.com;Username=postgres;Password=postgres;Database=postgres",
                                caCertificatePath);
                        }
                        else
                        {
                            UpdateVote(pgsql, vote.voter_id, vote.vote);
                        }
                    }
                    else
                    {
                        keepAliveCommand.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unexpected error: {ex}");
                return 1;
            }
        }

        private static NpgsqlConnection OpenDbConnection(string connectionString, string caCertificatePath)
        {
            NpgsqlConnection connection;

            while (true)
            {
                try
                {
                    var builder = new NpgsqlConnectionStringBuilder(connectionString)
                    {
                        SslMode = SslMode.VerifyFull, // 인증서를 사용하여 SSL 검증
                        TrustServerCertificate = false
                    };

                    connection = new NpgsqlConnection(builder.ConnectionString);
                    connection.ProvideClientCertificatesCallback += certs =>
                    {
                        // 인증서 파일 추가
                        certs.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(caCertificatePath));
                    };

                    connection.Open();
                    break;
                }
                catch (SocketException ex)
                {
                    Console.Error.WriteLine($"SocketException: {ex.Message}");
                    Thread.Sleep(1000);
                }
                catch (DbException ex)
                {
                    Console.Error.WriteLine($"DbException: {ex.Message}");
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Unexpected error while connecting to DB: {ex}");
                    Thread.Sleep(1000);
                }
            }

            Console.Error.WriteLine("Connected to db");

            var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS votes (
                                        id VARCHAR(255) NOT NULL UNIQUE,
                                        vote VARCHAR(255) NOT NULL
                                    )";
            command.ExecuteNonQuery();

            return connection;
        }

        private static ConnectionMultiplexer OpenRedisConnection(string hostname)
        {
            var ipAddress = GetIp(hostname);
            Console.WriteLine($"Found redis at {ipAddress}");

            while (true)
            {
                try
                {
                    Console.Error.WriteLine("Connecting to redis");
                    return ConnectionMultiplexer.Connect(ipAddress);
                }
                catch (RedisConnectionException ex)
                {
                    Console.Error.WriteLine($"RedisConnectionException: {ex.Message}");
                    Thread.Sleep(1000);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Unexpected error while connecting to Redis: {ex}");
                    Thread.Sleep(1000);
                }
            }
        }

        private static string GetIp(string hostname)
            => Dns.GetHostEntryAsync(hostname)
                .Result
                .AddressList
                .First(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToString();

        private static void UpdateVote(NpgsqlConnection connection, string voterId, string vote)
        {
            var command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO votes (id, vote) VALUES (@id, @vote)";
                command.Parameters.AddWithValue("@id", voterId);
                command.Parameters.AddWithValue("@vote", vote);
                command.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                Console.Error.WriteLine($"DbException during UpdateVote: {ex.Message}");
                command.CommandText = "UPDATE votes SET vote = @vote WHERE id = @id";
                command.ExecuteNonQuery();
            }
            finally
            {
                command.Dispose();
            }
        }
    }
}
