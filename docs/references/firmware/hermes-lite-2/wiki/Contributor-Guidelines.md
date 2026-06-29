
These contributor guidelines are required as more Hermes-Lite 2.0 units are deployed, and more people become interested in contributing to and enhancing the Hermes-Lite 2.0. The intent of these guidelines is to coordinate contributions in a sensible and sustainable way, and ensure they remain compatibile with other contributions. This reduces user confusion and eases software and hardware development burdens as all changes are tracked and coordinated in a single way and place. These guidelines are based on common and established code development principles.

## "One-Off" Contributions

The Hermes-Lite 2.0 is an experimental open-source project. "One-off" changes by one to a handful of people are encouraged. Such small group grassroots experiments can be the impetus for later larger audience changes. Please share what you are doing with the [group](https://groups.google.com/forum/#!forum/hermes-lite). There is no need to follow the later guidelines for larger contributions, but it is still encouraged.

## Larger Contributions

Once your contribution has about 5 adopters, and you would like or expect to have more, then additional coordination is required. Please start a [group](https://groups.google.com/forum/#!forum/hermes-lite) thread expressing your intent and addressing these topics. For acceptance, you must obtain the approval (or at least no rejection) from past significant contributors to HL2 hardware, gateware or software. You also must obtain final approval from the project lead and owner, KF7O.

### IO Requirements and Changes

What IO requirements or IO changes does your contribution make? How will this play nicely with other IO usage, both with standard gateware and gateware with other options enabled? Note that this does not exclude repurposing IO usage in a different way. The discussion is to find ways to minimize the impact of your contribution's IO requirements versus other ways the IO might be used.

### Gateware Requirements and Changes

What gateware changes does your contribution require? How will this play nicely with the existing standard gateware and gateware with other options enabled? Is your contribution an option that is enabled for a special gateware build, or should it be included in all gateware builds? How have you integrated your changes into the main github branch?

It is the contributors responsibility to submit or find someone to submit any gateware changes to the main repository. The Hermes-Lite 2.0 project follows the standard [git pull request](https://help.github.com/en/articles/about-pull-requests) paradigm for accepting changes to the gateware. If your changes are just a handful of lines, the changed file(s) or a patch can be sent directly to the group. This should be done in a timely manner so that your changes do not become stale.

New gateware contributions must use [Verilog generate](https://www.verilogpro.com/verilog-generate-configurable-rtl/) preferably or [Verilog preprocessor directives](https://www.veripool.org/papers/Preproc_Good_Evil_SNUGBos10_paper.pdf) to enable or disable any new features or changed IO assignments. Only when your contribution is accepted for all builds will these "configurator switches" be removed. It is unlikely that any change to the Verilog gateware, no matter how controversial, will be rejected if it cleanly includes these configurator switches and modularizes all additions. See the Verilog section below for more details on writing Verilog.

All gateware releases are made with an automated Makefile system. If there is enough interest from 10 or more users, your set of configurator switches will be added to the Makefile and a gateware variant supporting your contributions will be included in the release. Ideally, your contributions may become part of the standard gateware release. You also may release your own gateware provided your fork's patches are submitted to the main repository in a timely matter, 1 to 2 weeks after you finalize any changes. 

### Software Requirements and Changes

What changes to software are necessary for your contribution to work? Have you already made and tested any required software changes? If not, what is your plan to do so? Do you have a commitment from a software developer to make any required software changes? Does your contribution play nicely with existing software and other software features?

### Support and Documentation 

How will users obtain support for your contribution? Will you be available on the [group](https://groups.google.com/forum/#!forum/hermes-lite) to answer questions? Have you created a wiki page or other easily accessible documentation for your contribution?

### Hardware Access

If your contribution requires new hardware, how will a user obtain that hardware? Is that documented with a clear schematic, BOM and assembly procedure? 

Sales of Hermes-Lite 2.0 related hardware is allowed on the [group](https://groups.google.com/forum/#!forum/hermes-lite) provided it is for an approved project, and your charge to a user is your out-of-pocket costs plus no more than ~20% for your handling.


## Verilog

The Hermes-Lite 2.0 gateware is written in Verilog, a industry-standard hardware description language. Contributions to the gateware will require a good understanding of Verilog. Here are a few pointers for learning Verilog.

The free site [EDA Playground](https://www.edaplayground.com/) offers online access to various Verilog compilers, simulators and examples. you can practice and test Verilog by writing short bits of code online and then simulating. It is a great tool to use to learn Verilog. As part of my interview process, I always have candidates go to this site and write some specific Verilog modules for me.

There are numerous videos on YouTube that can help you learn Verilog. For HL2 gateware development, you should focus on SystemVerilog or Verilog for logic-level design. Verilog is used for other applications too such as testbenches, low-level VLSI modeling, and more. These uses are not required for the HL2 gateware. Some videos to get your started are [Verilog HDL Basics](https://www.youtube.com/watch?v=PJGvZSlsLKs) and this [IIT Lecture Series](https://www.youtube.com/watch?v=FWE0-FOoE4s&list=PLUtfVcb-iqn-EkuBs3arreilxa2UKIChl).

You may also find good online courses for Verilog. Just do a google search. Stanford, MIT, CMU and other universities may be running courses that teach some Verilog. The OpenHPSDR project has also prepared a [Verilog lecture and lab series](http://verilog.openhpsdr.org/) with special attention paid to the use and architecture of FPGA-based SDR devices and the OpenHPSDR architecture in particular.

There are also many good books and documents on Verilog or SystemVerilog. Again, look for books that focus on Verilog or SystemVerilog for design (not testing), and in particular FPGA use. I particularly appreciate the resources at [Sutherland HDL](http://www.sutherland-hdl.com/index.html).

Intel's Quartus, which is used for Synthesis, also includes an IDE if your prefer that development route. I prefer the [Sublime](https://www.sublimetext.com/) text editor with the [SystemVerilog Plugin](https://sv-doc.readthedocs.io/en/latest/).

Learning Verilog takes time and effort. You can't expect a quick answer from the group. You will have to use the resources above, and then study and practice.










