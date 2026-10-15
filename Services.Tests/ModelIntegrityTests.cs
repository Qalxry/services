using System.Reflection;
using Services.Core.Models;
using Xunit;

namespace Services.Tests
{
    /// <summary>
    /// 模型完整性守卫：通过反射保证新增字段时不会遗漏 Service.CopyFrom 与 ServiceConfig.FromService。
    /// 若这里失败，说明新增了属性但没有同步更新对应的复制逻辑。
    /// </summary>
    public class ModelIntegrityTests
    {
        private static List<PropertyInfo> WritableProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.SetMethod is { IsPublic: true })
                .ToList();

        /// <summary>
        /// 为属性构造一个与默认值不同的非默认值，确保“漏复制”能被检测出来
        /// </summary>
        private static object NonDefaultValue(PropertyInfo property, object defaultInstance, int seed)
        {
            var type = property.PropertyType;
            var current = property.GetValue(defaultInstance);

            if (type == typeof(string)) return $"{property.Name}-{seed}";
            if (type == typeof(bool)) return !(bool)current!;
            if (type == typeof(int)) return (int)current! + 100 + seed;
            if (type == typeof(DateTime)) return new DateTime(2024, 1, 1).AddDays(seed);
            if (type.IsEnum)
            {
                return Enum.GetValues(type).Cast<object>().First(v => !Equals(v, current));
            }
            if (type == typeof(IReadOnlyDictionary<string, string>))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [$"{property.Name}Key{seed}"] = $"value-{seed}" };
            }

            if (type == typeof(IReadOnlyList<string>))
            {
                return new List<string> { $"{property.Name}Item{seed}", $"{property.Name}Other{seed}" };
            }

            throw new NotSupportedException(
                $"测试辅助方法尚不支持属性 {property.DeclaringType!.Name}.{property.Name} 的类型 {type}，请扩展 NonDefaultValue。");
        }

        /// <summary>
        /// 属性值比较：字典与字符串列表按内容比较，其余按 Equals
        /// </summary>
        private static bool ValuesEqual(object? left, object? right)
        {
            if (left is IReadOnlyDictionary<string, string> a && right is IReadOnlyDictionary<string, string> b)
                return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && value == pair.Value);
            if (left is IReadOnlyList<string> x && right is IReadOnlyList<string> y)
                return x.SequenceEqual(y);
            return Equals(left, right);
        }

        private static void FillWithNonDefaultValues(object target, IEnumerable<PropertyInfo> properties)
        {
            var defaults = Activator.CreateInstance(target.GetType())!;
            int seed = 1;
            foreach (var property in properties)
            {
                property.SetValue(target, NonDefaultValue(property, defaults, seed++));
            }
        }

        [Fact]
        public void CopyFrom_CopiesEveryWritablePropertyExceptId()
        {
            var properties = WritableProperties(typeof(Service));
            var source = new Service { Id = "source-id" };
            FillWithNonDefaultValues(source, properties.Where(p => p.Name != nameof(Service.Id)));
            var target = new Service { Id = "target-id" };

            target.CopyFrom(source);

            var missed = properties
                .Where(p => p.Name != nameof(Service.Id))
                .Where(p => !ValuesEqual(p.GetValue(source), p.GetValue(target)))
                .Select(p => p.Name)
                .ToList();
            Assert.True(missed.Count == 0, "Service.CopyFrom 未复制以下属性：" + string.Join(", ", missed));
        }

        [Fact]
        public void CopyFrom_DoesNotShareEnvironmentInstance()
        {
            var source = new Service { Environment = new Dictionary<string, string> { ["A"] = "1" } };
            var target = new Service();

            target.CopyFrom(source);

            Assert.NotSame(source.Environment, target.Environment);
            Assert.Equal("1", target.Environment["A"]);
        }

        [Fact]
        public void FromService_DoesNotShareEnvironmentInstance()
        {
            var service = new Service { Environment = new Dictionary<string, string> { ["A"] = "1" } };

            var config = ServiceConfig.FromService(service);

            Assert.NotSame(service.Environment, config.Environment);
            Assert.Equal("1", config.Environment["A"]);
        }

        [Fact]
        public void Environment_DefaultsToEmptyAndIsCaseInsensitive()
        {
            Assert.Empty(new Service().Environment);
            Assert.Empty(new ServiceConfig().Environment);

            var config = ServiceConfig.FromService(new Service { Environment = new Dictionary<string, string> { ["Path"] = "x" } });
            Assert.Equal("x", config.Environment["PATH"]);
        }

        [Fact]
        public void CopyFrom_DoesNotShareDependenciesInstance()
        {
            var source = new Service { Dependencies = new List<string> { "Tcpip" } };
            var target = new Service();

            target.CopyFrom(source);

            Assert.NotSame(source.Dependencies, target.Dependencies);
            Assert.Equal(new[] { "Tcpip" }, target.Dependencies);
        }

        [Fact]
        public void FromService_DoesNotShareDependenciesInstance()
        {
            var service = new Service { Dependencies = new List<string> { "Tcpip" } };

            var config = ServiceConfig.FromService(service);

            Assert.NotSame(service.Dependencies, config.Dependencies);
            Assert.Equal(new[] { "Tcpip" }, config.Dependencies);
        }

        [Fact]
        public void DependenciesAndDelayedAutoStart_DefaultToOldBehavior()
        {
            Assert.Empty(new Service().Dependencies);
            Assert.Empty(new ServiceConfig().Dependencies);
            Assert.NotSame(new Service().Dependencies, new Service().Dependencies);
            Assert.False(new Service().DelayedAutoStart);
            Assert.False(new ServiceConfig().DelayedAutoStart);
        }

        [Fact]
        public void CopyFrom_CopiesDelayedAutoStart()
        {
            var target = new Service();

            target.CopyFrom(new Service { StartupType = ServiceStartupType.Auto, DelayedAutoStart = true });

            Assert.True(target.DelayedAutoStart);
            Assert.True(target.AutoStart);
        }

        [Fact]
        public void CopyFrom_DoesNotOverwriteId()
        {
            var source = new Service { Id = "source-id", Name = "a" };
            var target = new Service { Id = "target-id" };

            target.CopyFrom(source);

            Assert.Equal("target-id", target.Id);
            Assert.Equal("a", target.Name);
        }

        [Fact]
        public void CopyFrom_RaisesPropertyChangedForChangedFields()
        {
            var source = new Service { Name = "new", State = ServiceRunState.Running, Pid = 42 };
            var target = new Service();
            var changed = new List<string?>();
            target.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            target.CopyFrom(source);

            Assert.Contains(nameof(Service.Name), changed);
            Assert.Contains(nameof(Service.State), changed);
            Assert.Contains(nameof(Service.Pid), changed);
            Assert.Contains(nameof(Service.HasPid), changed);
        }

        [Fact]
        public void FromService_AssignsEveryServiceConfigProperty()
        {
            var serviceProperties = typeof(Service)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name);
            var configProperties = WritableProperties(typeof(ServiceConfig));

            // ServiceConfig 的每个可写属性都必须能在 Service 上找到同名同类型的属性
            foreach (var configProperty in configProperties)
            {
                Assert.True(serviceProperties.TryGetValue(configProperty.Name, out var serviceProperty),
                    $"Service 上缺少与 ServiceConfig.{configProperty.Name} 同名的属性");
                Assert.Equal(configProperty.PropertyType, serviceProperty!.PropertyType);
            }

            var service = new Service();
            FillWithNonDefaultValues(service, configProperties.Select(p => serviceProperties[p.Name]).Where(p => p.CanWrite));

            var config = ServiceConfig.FromService(service);

            var missed = configProperties
                .Where(p => !ValuesEqual(serviceProperties[p.Name].GetValue(service), p.GetValue(config)))
                .Select(p => p.Name)
                .ToList();
            Assert.True(missed.Count == 0, "ServiceConfig.FromService 未正确赋值以下属性：" + string.Join(", ", missed));
        }

        [Fact]
        public void FromService_ConfigValuesDifferFromConfigDefaults()
        {
            // 防止守卫失效：构造的值必须与 ServiceConfig 默认值不同，否则漏赋值也会通过
            var configProperties = WritableProperties(typeof(ServiceConfig));
            var serviceProperties = typeof(Service)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .ToDictionary(p => p.Name);
            var service = new Service();
            FillWithNonDefaultValues(service, configProperties.Select(p => serviceProperties[p.Name]).Where(p => p.CanWrite));
            var defaults = new ServiceConfig();

            var config = ServiceConfig.FromService(service);

            foreach (var property in configProperties)
            {
                Assert.False(ValuesEqual(property.GetValue(defaults), property.GetValue(config)),
                    $"属性 {property.Name} 的测试值与默认值相同，无法检测漏赋值");
            }
        }
    }
}
