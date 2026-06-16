	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"global-rw.c"
	.section	.text.global_rw,"ax",@progbits
	.globl	global_rw                       ; -- Begin function global_rw
	.type	global_rw,@function
global_rw:                              ; @global_rw
; %bb.0:
	clc
	adc	counter
	tay
	txa
	adc	counter+1
	tax
	tya
	sty	counter
	stx	counter+1
	rts
.Lfunc_end0:
	.size	global_rw, .Lfunc_end0-global_rw
                                        ; -- End function
	.type	counter,@object                 ; @counter
	.section	.bss.counter,"aw",@nobits
	.globl	counter
counter:
	.short	0                               ; 0x0
	.size	counter, 2

	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that there is something in .bss, so it may need to be zeroed.
	.globl	__do_zero_bss
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
