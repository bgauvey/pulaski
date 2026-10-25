using Pulaski.Application.MachineData;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>What each connector reads (D-050, #22, #24): its settings and the addresses it takes.</summary>
public class EdgeProtocolsTests
{
    [Theory]
    [InlineData("opc_ua", "ns=2;s=Line1.State", true)]
    [InlineData("opc_ua", "ns=3;i=1001", true)]
    [InlineData("opc_ua", "i=2258", true)]
    [InlineData("opc_ua", "nsu=http://plant1/ua;s=Extruder/Melt.Temp", true)]
    [InlineData("opc_ua", "ns=2;g=0a5b3c2d-1e2f-4a5b-8c9d-0e1f2a3b4c5d", true)]
    [InlineData("opc_ua", "Line1.State", false)]
    [InlineData("opc_ua", "ns=x;s=A", false)]
    [InlineData("ethernet_ip", "Line1_State", true)]
    [InlineData("ethernet_ip", "Program:Main.State|dint", true)]
    [InlineData("ethernet_ip", "Counts[3]|udint", true)]
    [InlineData("ethernet_ip", "Status.Bits.5|bool", true)]
    [InlineData("ethernet_ip", "Melt.Temp|real", true)]
    [InlineData("ethernet_ip", "Melt.Temp|float", false)]
    [InlineData("ethernet_ip", "1Bad", false)]
    [InlineData("ethernet_ip", "Plant1/Node:metric", false)]
    [InlineData("sparkplug_b", "Plant1/Node/Device:Status/StateCurrent", true)]
    [InlineData("sparkplug_b", "ns=2;s=Line1.State", false)]
    public void Each_protocol_takes_its_own_addresses(string protocol, string address, bool valid) =>
        Assert.Equal(valid, EdgeProtocols.IsValidAddress(protocol, address));

    [Fact]
    public void Opc_ua_settings_need_an_opc_tcp_endpoint_and_known_security()
    {
        Assert.Empty(EdgeProtocols.SettingsProblems("opc_ua", new Dictionary<string, string> { ["endpoint"] = "opc.tcp://plc1:4840", ["securityMode"] = "sign" }));
        Assert.Equal(4, EdgeProtocols.SettingsProblems("opc_ua", new Dictionary<string, string>
        {
            ["endpoint"] = "http://plc1",
            ["securityMode"] = "maximum",
            ["serverThumbprint"] = "ABC",
            ["publishingIntervalMs"] = "5",
        }).Count);
        Assert.Contains(EdgeProtocols.SettingsProblems("opc_ua", new Dictionary<string, string> { ["host"] = "x", ["endpoint"] = "opc.tcp://a:1" }),
            p => p.Contains("isn't a OPC UA setting", StringComparison.Ordinal));
    }

    [Fact]
    public void Ethernet_ip_settings_need_a_gateway_and_a_controllogix_its_path()
    {
        Assert.Empty(EdgeProtocols.SettingsProblems("ethernet_ip", new Dictionary<string, string> { ["gateway"] = "10.0.0.20", ["path"] = "1,0" }));
        Assert.Empty(EdgeProtocols.SettingsProblems("ethernet_ip", new Dictionary<string, string> { ["gateway"] = "10.0.0.21", ["plc"] = "micro800" }));
        Assert.Single(EdgeProtocols.SettingsProblems("ethernet_ip", new Dictionary<string, string> { ["gateway"] = "10.0.0.20" }));
        Assert.Equal(3, EdgeProtocols.SettingsProblems("ethernet_ip", new Dictionary<string, string>
        {
            ["gateway"] = "10.0.0.20",
            ["plc"] = "s7",
            ["path"] = "1,x",
            ["pollIntervalMs"] = "0",
        }).Count);
        Assert.Single(EdgeProtocols.SettingsProblems("modbus", new Dictionary<string, string>()));
    }
}
