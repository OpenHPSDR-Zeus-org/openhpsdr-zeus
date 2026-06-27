# Make datetime.v from Tcl script

# Current date, time, and seconds since epoch
# Array index                                            0  1  2  3  4  5  6
set datetime_arr [clock format [clock seconds] -format {%Y %m %d %H %M %S %s}]

set filename datetime.v
set file [open $filename w]


module iambic (
                                input clock,    
                                input [5:0] cw_speed,                   // 1 to 60 WPM
                                input iambic,                                           // 0 = straight/bug,  1 = Iambic 
                                input keyer_mode,                                       // 0 = Mode A, 1 = Mode B
                                input [7:0] weight,                             // 33 to 66, nominal is 50
                                input letter_space,                             // 0 = off, 1 = on
                                input dot_key,                                          // dot paddle  input, active high
                                input dash_key,                                 // dash paddle input, active high
                                input CWX,                                                 // CW data from PC active high
                                input paddle_swap,                              // swap if set
                                output reg keyer_out,                   // keyer output, active high
                                input IO5                                                       // additional CW key via digital input IO5, debounced, inverted
                                );

1262 reg [4:0] day;
1263 reg [3:0] month;
1264 reg [4:0] year;
    
parameter   clock_speed = 30;                                   

puts $file "module datetime ("
puts $file "output reg [4:0] day;"
puts $file "output reg [3:0] month;"
puts $file "output reg [4:0] year;"
puts $file ")"
puts $file ""
puts $file "parameter 
puts $file "  constant YEAR_INT  : integer                       := [lindex $datetime_arr 0];"
puts $file "  constant YEAR_HEX  : std_logic_vector(15 downto 0) := X\"[lindex $datetime_arr 0]\";"
puts $file "  constant MONTH_INT : integer                       := [lindex $datetime_arr 1];"
puts $file "  constant MONTH_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 1]\";"
puts $file "  constant DAY_INT   : integer                       := [lindex $datetime_arr 2];"
puts $file "  constant DAY_HEX   : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 2]\";"
puts $file "  constant DATE_HEX  : std_logic_vector(31 downto 0) := YEAR_HEX & MONTH_HEX & DAY_HEX;"
puts $file "  -- Time information"
puts $file "  constant HOUR_INT   : integer                       := [lindex $datetime_arr 3];"
puts $file "  constant HOUR_HEX   : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 3]\";"
puts $file "  constant MINUTE_INT : integer                       := [lindex $datetime_arr 4];"
puts $file "  constant MINUTE_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 4]\";"
puts $file "  constant SECOND_INT : integer                       := [lindex $datetime_arr 5];"
puts $file "  constant SECOND_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 5]\";"
puts $file "  constant TIME_HEX   : std_logic_vector(31 downto 0) := X\"00\" & HOUR_HEX & MINUTE_HEX & SECOND_HEX;"
puts $file "  -- Miscellaneous information"
puts $file "  constant EPOCH_INT  : integer := [lindex $datetime_arr 6];  -- Seconds since 1970-01-01_00:00:00"
puts $file "end package;"
close $file

